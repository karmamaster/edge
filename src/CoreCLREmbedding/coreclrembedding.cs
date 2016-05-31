using System;
using System.Linq;
using System.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Linq.Expressions;
using System.Dynamic;
using System.Collections.Generic;
using System.Collections;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using Microsoft.DotNet.ProjectModel;
using Microsoft.DotNet.ProjectModel.Compilation;
using Microsoft.DotNet.ProjectModel.Graph;
using Microsoft.Extensions.DependencyModel;
using NuGet.Frameworks;
using DotNetRuntimeEnvironment = Microsoft.DotNet.InternalAbstractions.RuntimeEnvironment;
using Semver;

[StructLayout(LayoutKind.Sequential)]
// ReSharper disable once CheckNamespace
public struct V8ObjectData
{
    public int propertiesCount;
    public IntPtr propertyTypes;
    public IntPtr propertyNames;
    public IntPtr propertyValues;
}

[StructLayout(LayoutKind.Sequential)]
public struct V8ArrayData
{
    public int arrayLength;
    public IntPtr itemTypes;
    public IntPtr itemValues;
}

[StructLayout(LayoutKind.Sequential)]
public struct V8BufferData
{
    public int bufferLength;
    public IntPtr buffer;
}

public enum V8Type
{
    Function = 1,
    Buffer = 2,
    Array = 3,
    Date = 4,
    Object = 5,
    String = 6,
    Boolean = 7,
    Int32 = 8,
    UInt32 = 9,
    Number = 10,
    Null = 11,
    Task = 12,
    Exception = 13
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct EdgeBootstrapperContext
{
    [MarshalAs(UnmanagedType.LPStr)]
    public string RuntimeDirectory;

    [MarshalAs(UnmanagedType.LPStr)]
    public string ApplicationDirectory;

    [MarshalAs(UnmanagedType.LPStr)]
    public string EdgeNodePath;

    [MarshalAs(UnmanagedType.LPStr)]
    public string BootstrapAssemblies;

    [MarshalAs(UnmanagedType.LPStr)]
    public string DependencyManifestFile;
}

public delegate void CallV8FunctionDelegate(IntPtr payload, int payloadType, IntPtr v8FunctionContext, IntPtr callbackContext, IntPtr callbackDelegate);
public delegate void TaskCompleteDelegate(IntPtr result, int resultType, int taskState, IntPtr context);

[SecurityCritical]
public class CoreCLREmbedding
{
    private class TaskState
    {
        public readonly TaskCompleteDelegate Callback;
        public readonly IntPtr Context;

        public TaskState(TaskCompleteDelegate callback, IntPtr context)
        {
            Callback = callback;
            Context = context;
        }
    }

    private class EdgeRuntimeEnvironment
    {
        public EdgeRuntimeEnvironment(EdgeBootstrapperContext bootstrapperContext)
        {
            ApplicationDirectory = bootstrapperContext.ApplicationDirectory;
            RuntimePath = bootstrapperContext.RuntimeDirectory;
            EdgeNodePath = bootstrapperContext.EdgeNodePath;
            DependencyManifestFile = bootstrapperContext.DependencyManifestFile;
        }

        public string RuntimePath
        {
            get;
        }

        public string ApplicationDirectory
        {
            get;
        }

        public string EdgeNodePath
        {
            get;
        }

        public string DependencyManifestFile
        {
            get;
        }
    }

    private class EdgeAssemblyLoadContext : AssemblyLoadContext
    {
        internal readonly Dictionary<string, string> CompileAssemblies = new Dictionary<string, string>();
        private bool _noDependencyManifestFile = true;
        private readonly Dictionary<string, Assembly> _loadedAssemblies = new Dictionary<string, Assembly>();
        private readonly Dictionary<string, string> _libraries = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _nativeLibraries = new Dictionary<string, string>();
        private readonly string _packagesPath;
        private static string _targetFrameworkString = ".NETStandard,Version=v1.5";
        private static NuGetFramework _targetFramework = new NuGetFramework(".NETStandard,Version=v1.5");

        public EdgeAssemblyLoadContext(string bootstrapAssemblies)
        {
            DebugMessage("EdgeAssemblyLoadContext::ctor (CLR) - Starting");
            DebugMessage("EdgeAssemblyLoadContext::ctor (CLR) - Getting the packages path");

            _packagesPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");

            if (String.IsNullOrEmpty(_packagesPath))
            {
                string profileDirectory = Environment.GetEnvironmentVariable("USERPROFILE");

                if (String.IsNullOrEmpty(profileDirectory))
                {
                    profileDirectory = Environment.GetEnvironmentVariable("HOME");
                }

                _packagesPath = Path.Combine(profileDirectory, ".nuget", "packages");
            }

            DebugMessage("EdgeAssemblyLoadContext::ctor (CLR) - Packages path is {0}", _packagesPath);
            DebugMessage("EdgeAssemblyLoadContext::ctor (CLR) - Adding the bootstrap assemblies");

            string[] nameVersionPaths = bootstrapAssemblies.Split(';');

            foreach (string nameVersionPath in nameVersionPaths)
            {
                string[] pair = nameVersionPath.Split(':');
                string[] nameVersion = pair[0].Split('/');

                string name = nameVersion[0];
                string version = nameVersion[1];
                string path = Path.Combine(_packagesPath, name, version, pair[1].Replace('/', Path.DirectorySeparatorChar));

                _libraries[name] = path;
                DebugMessage("EdgeAssemblyLoadContext::ctor (CLR) - Added bootstrap assembly {0}", path);
            }

            DebugMessage("EdgeAssemblyLoadContext::ctor (CLR) - Finished");
        }

        public void LoadDependencyManifest(string dependencyManifestFile)
        {
            DebugMessage("EdgeAssemblyLoadContext::LoadDependencyManifest (CLR) - Loading dependency manifest from {0}", dependencyManifestFile);
            
            LockFile edgeJsProjectJsonLockFile = LockFileReader.Read(Path.Combine(RuntimeEnvironment.EdgeNodePath, "project.lock.json"), false);
            ProjectContext edgeJsProjectContext = new ProjectContextBuilder().WithLockFile(edgeJsProjectJsonLockFile).WithTargetFramework(_targetFrameworkString).Build();
            LibraryExporter edgeJsProjectExporter = edgeJsProjectContext.CreateExporter("Release");
            DependencyContext edgeJsDependencyContext = new DependencyContextBuilder().Build(null, null, edgeJsProjectExporter.GetAllExports(), true, _targetFramework,
                String.Empty);
            
            DependencyContextJsonReader dependencyContextReader = new DependencyContextJsonReader();

            using (FileStream dependencyManifestStream = new FileStream(dependencyManifestFile, FileMode.Open))
            {
                DebugMessage("EdgeAssemblyLoadContext::LoadDependencyManifest (CLR) - Reading dependency manifest file and merging in dependencies from Edge.js");
                DependencyContext dependencyContext = dependencyContextReader.Read(dependencyManifestStream).Merge(edgeJsDependencyContext);

                DebugMessage("EdgeAssemblyLoadContext::LoadDependencyManifest (CLR) - Resetting assemblies list");
                _libraries.Clear();

                AddDependencies(dependencyContext);
            }

            _noDependencyManifestFile = false;

            string entryAssemblyPath = dependencyManifestFile.Replace(".deps.json", ".dll");

            if (File.Exists(entryAssemblyPath))
            {
                Assembly entryAssembly = Load(new AssemblyName(Path.GetFileNameWithoutExtension(entryAssemblyPath)));
                Lazy<DependencyContext> defaultDependencyContext = new Lazy<DependencyContext>(() => DependencyContext.Load(entryAssembly));

                // I really don't like doing it this way, but it's the easiest way to give the running code access to the default 
                // dependency context data
                typeof(DependencyContext).GetField("_defaultContext", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, defaultDependencyContext);
            }

            DebugMessage("EdgeAssemblyLoadContext::LoadDependencyManifest (CLR) - Finished");
        }

        private void AddDependencies(DependencyContext dependencyContext)
        {
            DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Adding dependencies for {0}", dependencyContext.Target.Framework);

            foreach (CompilationLibrary compileLibrary in dependencyContext.CompileLibraries)
            {
                DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Processing compile dependency {0}", compileLibrary.Name);

                if (compileLibrary.Assemblies == null || compileLibrary.Assemblies.Count == 0)
                {
                    continue;
                }

                string assemblyPath = Path.Combine(_packagesPath, compileLibrary.Name, compileLibrary.Version, compileLibrary.Assemblies[0].Replace('/', Path.DirectorySeparatorChar));

                if (!CompileAssemblies.ContainsKey(compileLibrary.Name))
                {
                    CompileAssemblies[compileLibrary.Name] = assemblyPath;
                    DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Added compile assembly {0}", assemblyPath);
                }

                else
                {
                    DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Already present in the compile assemblies list, skipping");
                }
            }

            foreach (RuntimeLibrary runtimeLibrary in dependencyContext.RuntimeLibraries)
            {
                DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Processing runtime dependency {1} {0}", runtimeLibrary.Name, runtimeLibrary.Type);

                if (runtimeLibrary.RuntimeAssemblyGroups != null && runtimeLibrary.RuntimeAssemblyGroups.Any() && runtimeLibrary.RuntimeAssemblyGroups[0].AssetPaths.Any())
                {
                    string assetPath = runtimeLibrary.RuntimeAssemblyGroups[0].AssetPaths[0];
                    string assemblyPath = runtimeLibrary.Type == "project"
                        ? Path.Combine(RuntimeEnvironment.ApplicationDirectory, assetPath)
                        : Path.Combine(_packagesPath, runtimeLibrary.Name, runtimeLibrary.Version, assetPath.Replace('/', Path.DirectorySeparatorChar));
                    string libraryNameFromPath = Path.GetFileNameWithoutExtension(assemblyPath);

                    if (!_libraries.ContainsKey(runtimeLibrary.Name))
                    {
                        _libraries[runtimeLibrary.Name] = assemblyPath;
                        DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Added runtime assembly {0}", assemblyPath);
                    }

                    else
                    {
                        DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Already present in the runtime assemblies list, skipping");
                    }

                    if (runtimeLibrary.Name != libraryNameFromPath && !_libraries.ContainsKey(libraryNameFromPath))
                    {
                        _libraries[libraryNameFromPath] = assemblyPath;
                        DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Filename in the dependency context did not match the package/project name, added additional resolver for {0}", libraryNameFromPath);
                    }

                    if (!CompileAssemblies.ContainsKey(runtimeLibrary.Name))
                    {
                        CompileAssemblies[runtimeLibrary.Name] = assemblyPath;
                        DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Added compile assembly {0}", assemblyPath);
                    }

                    else
                    {
                        DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Already present in the compile assemblies list, skipping");
                    }

                    if (runtimeLibrary.Name != libraryNameFromPath && !CompileAssemblies.ContainsKey(libraryNameFromPath))
                    {
                        CompileAssemblies[libraryNameFromPath] = assemblyPath;
                        DebugMessage("EdgeAssemblyLoadContext::AddCompileDependencies (CLR) - Filename in the dependency context did not match the package/project name, added additional resolver for {0}", libraryNameFromPath);
                    }
                }

                List<string> nativeAssemblies = runtimeLibrary.GetRuntimeNativeAssets(dependencyContext, DotNetRuntimeEnvironment.GetRuntimeIdentifier()).ToList();

                if (nativeAssemblies.Any())
                {
                    DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Adding native dependencies for {0}", DotNetRuntimeEnvironment.GetRuntimeIdentifier());

                    foreach (string nativeAssembly in nativeAssemblies)
                    {
                        string nativeAssemblyPath = Path.Combine(_packagesPath, runtimeLibrary.Name, runtimeLibrary.Version, nativeAssembly.Replace('/', Path.DirectorySeparatorChar));

                        DebugMessage("EdgeAssemblyLoadContext::AddDependencies (CLR) - Adding native assembly {0} at {1}",
                            Path.GetFileNameWithoutExtension(nativeAssembly), nativeAssemblyPath);
                        _nativeLibraries[Path.GetFileNameWithoutExtension(nativeAssembly)] = nativeAssemblyPath;
                    }
                }
            }
        }

        internal void AddCompiler(string compilerDirectory)
        {
            DebugMessage("EdgeAssemblyLoadContext::AddCompiler (CLR) - Adding the compiler in {0}", compilerDirectory);
            
            LockFile compilerProjectJsonLockFile = LockFileReader.Read(Path.Combine(compilerDirectory, "project.lock.json"), false);
            ProjectContext compilerProjectContext = new ProjectContextBuilder().WithLockFile(compilerProjectJsonLockFile).WithTargetFramework(_targetFrameworkString).Build();
            LibraryExporter compilerProjectExporter = compilerProjectContext.CreateExporter("Release");
            DependencyContext compilerDependencyContext = new DependencyContextBuilder().Build(null, compilerProjectExporter.GetAllExports(), compilerProjectExporter.GetAllExports(), true, _targetFramework,
                String.Empty);

            DebugMessage("EdgeAssemblyLoadContext::AddCompiler (CLR) - Adding dependencies for the compiler");
            AddDependencies(compilerDependencyContext);

            DebugMessage("EdgeAssemblyLoadContext::AddCompiler (CLR) - Finished");
        }

        [SecuritySafeCritical]
        protected override Assembly Load(AssemblyName assemblyName)
        {
            DebugMessage("EdgeAssemblyLoadContext::Load (CLR) - Trying to load {0}", assemblyName.Name);

            if (_loadedAssemblies.ContainsKey(assemblyName.Name))
            {
                DebugMessage("EdgeAssemblyLoadContext::Load (CLR) - Returning previously loaded assembly");
                return _loadedAssemblies[assemblyName.Name];
            }

            if (_libraries.ContainsKey(assemblyName.Name))
            {
                try
                {
                    Assembly assembly = LoadFromFile(_libraries[assemblyName.Name]);

                    if (assembly != null)
                    {
                        DebugMessage("EdgeAssemblyLoadContext::Load (CLR) - Successfully resolved assembly to {0}", _libraries[assemblyName.Name]);
                        _loadedAssemblies[assemblyName.Name] = assembly;

                        return assembly;
                    }
                }

                catch (Exception e)
                {
                    DebugMessage("EdgeAssemblyLoadContext::Load (CLR) - Error trying to load {0}: {2}{3}{4}", assemblyName.Name, e.Message, Environment.NewLine, e.StackTrace);
                    throw;
                }
            }

            DebugMessage("EdgeAssemblyLoadContext::Load (CLR) - Unable to resolve assembly from manifest list");

            if (_noDependencyManifestFile)
            {
                throw new Exception(
                    String.Format(
                        "Could not load file or assembly '{0}'.  You will need to create a project.json file that includes this dependency, run 'dotnet build' to generate a .deps.json file, and then specify the output directory from the build process using the EDGE_APP_ROOT environment variable.",
                        assemblyName.Name));
            }

            else
            {
                throw new Exception(
                    String.Format(
                        "Could not load file or assembly '{0}'.  You will need to add a reference to this package to your project.json.",
                        assemblyName.Name));
            }
        }

        public Assembly LoadFromFile(string assemblyPath)
        {
            if (!Path.IsPathRooted(assemblyPath))
            {
                assemblyPath = Path.Combine(RuntimeEnvironment.ApplicationDirectory, assemblyPath);
            }

            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException("Assembly file not found.", assemblyPath);
            }

            return LoadFromAssemblyPath(assemblyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            DebugMessage("EdgeAssemblyLoadContext::LoadUnmanagedDll (CLR) - Trying to resolve {0}", unmanagedDllName);

            if (_nativeLibraries.ContainsKey(unmanagedDllName))
            {
                DebugMessage("EdgeAssemblyLoadContext::LoadUnmanagedDll (CLR) - Successfully resolved to {0}", _nativeLibraries[unmanagedDllName]);
                return LoadUnmanagedDllFromPath(_nativeLibraries[unmanagedDllName]);
            }

            else
            {
                DebugMessage("EdgeAssemblyLoadContext::LoadUnmanagedDll (CLR) - Unable to resolve to any native library from the dependency manifest");
                return base.LoadUnmanagedDll(unmanagedDllName);
            }
        }
    }
    
    // ReSharper disable InconsistentNaming
    private static EdgeRuntimeEnvironment RuntimeEnvironment;
    private static EdgeAssemblyLoadContext LoadContext;
    // ReSharper enable InconsistentNaming

    private static readonly bool DebugMode = Environment.GetEnvironmentVariable("EDGE_DEBUG") == "1";
    private static readonly long MinDateTimeTicks = 621355968000000000;
    private static readonly Dictionary<Type, List<Tuple<string, Func<object, object>>>> TypePropertyAccessors = new Dictionary<Type, List<Tuple<string, Func<object, object>>>>();
    private static readonly int PointerSize = Marshal.SizeOf<IntPtr>();
    private static readonly int V8BufferDataSize = Marshal.SizeOf<V8BufferData>();
    private static readonly int V8ObjectDataSize = Marshal.SizeOf<V8ObjectData>();
    private static readonly int V8ArrayDataSize = Marshal.SizeOf<V8ArrayData>();
    private static readonly Dictionary<string, Tuple<Type, MethodInfo>> Compilers = new Dictionary<string, Tuple<Type, MethodInfo>>();

    public static void Initialize(IntPtr context, IntPtr exception)
    {
        try
        {
            DebugMessage("CoreCLREmbedding::Initialize (CLR) - Starting");

            EdgeBootstrapperContext bootstrapperContext = Marshal.PtrToStructure<EdgeBootstrapperContext>(context);

            RuntimeEnvironment = new EdgeRuntimeEnvironment(bootstrapperContext);
            LoadContext = new EdgeAssemblyLoadContext(bootstrapperContext.BootstrapAssemblies);

            AssemblyLoadContext.Default.Resolving += Assembly_Resolving;

            if (!String.IsNullOrEmpty(RuntimeEnvironment.DependencyManifestFile))
            {
                LoadContext.LoadDependencyManifest(RuntimeEnvironment.DependencyManifestFile);
            }

            DebugMessage("CoreCLREmbedding::Initialize (CLR) - Complete");
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::Initialize (CLR) - Exception was thrown: {0}", e.Message);

            V8Type v8Type;
            Marshal.WriteIntPtr(exception, MarshalCLRToV8(e, out v8Type));
        }
    }

    private static Assembly Assembly_Resolving(AssemblyLoadContext arg1, AssemblyName arg2)
    {
        return LoadContext.LoadFromAssemblyName(arg2);
    }

    [SecurityCritical]
    public static IntPtr GetFunc(string assemblyFile, string typeName, string methodName, IntPtr exception)
    {
        try
        {
            Marshal.WriteIntPtr(exception, IntPtr.Zero);

            Assembly assembly;

            if (assemblyFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || assemblyFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (!Path.IsPathRooted(assemblyFile))
                {
                    assemblyFile = Path.Combine(Directory.GetCurrentDirectory(), assemblyFile);
                }

                assembly = LoadContext.LoadFromFile(assemblyFile);
            }

            else
            {
                assembly = LoadContext.LoadFromAssemblyName(new AssemblyName(assemblyFile));
            }

            DebugMessage("CoreCLREmbedding::GetFunc (CLR) - Assembly {0} loaded successfully", assemblyFile);

            ClrFuncReflectionWrap wrapper = ClrFuncReflectionWrap.Create(assembly, typeName, methodName);
            DebugMessage("CoreCLREmbedding::GetFunc (CLR) - Method {0}.{1}() loaded successfully", typeName, methodName);

            Func<object, Task<object>> wrapperFunc = wrapper.Call;
            GCHandle wrapperHandle = GCHandle.Alloc(wrapperFunc);

            return GCHandle.ToIntPtr(wrapperHandle);
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::GetFunc (CLR) - Exception was thrown: {0}", e.Message);

            V8Type v8Type;
            Marshal.WriteIntPtr(exception, MarshalCLRToV8(e, out v8Type));

            return IntPtr.Zero;
        }
    }

    [SecurityCritical]
    public static IntPtr CompileFunc(IntPtr v8Options, int payloadType, IntPtr exception)
    {
        try
        {
            Marshal.WriteIntPtr(exception, IntPtr.Zero);

            IDictionary<string, object> options = (IDictionary<string, object>)MarshalV8ToCLR(v8Options, (V8Type)payloadType);
            string compiler = (string)options["compiler"];

            if (!Path.IsPathRooted(compiler))
            {
                compiler = Path.Combine(Directory.GetCurrentDirectory(), compiler);
            }

            MethodInfo compileMethod;
            Type compilerType;

            if (!Compilers.ContainsKey(compiler))
            {
                LoadContext.AddCompiler(Directory.GetParent(compiler).FullName);

                Assembly compilerAssembly = LoadContext.LoadFromFile(compiler);
                DebugMessage("CoreCLREmbedding::CompileFunc (CLR) - Compiler assembly {0} loaded successfully", compiler);

                compilerType = compilerAssembly.GetType("EdgeCompiler");

                if (compilerType == null)
                {
                    throw new TypeLoadException("Could not load type 'EdgeCompiler'");
                }

                compileMethod = compilerType.GetMethod("CompileFunc", BindingFlags.Instance | BindingFlags.Public);

                if (compileMethod == null)
                {
                    throw new Exception("Unable to find the CompileFunc() method on " + compilerType.FullName + ".");
                }

                MethodInfo setAssemblyLoader = compilerType.GetMethod("SetAssemblyLoader", BindingFlags.Static | BindingFlags.Public);

                setAssemblyLoader?.Invoke(null, new object[]
                {
                    new Func<Stream, Assembly>(assemblyStream => LoadContext.LoadFromStream(assemblyStream, null))
                });

                Compilers[compiler] = new Tuple<Type, MethodInfo>(compilerType, compileMethod);
            }

            else
            {
                compilerType = Compilers[compiler].Item1;
                compileMethod = Compilers[compiler].Item2;
            }

            object compilerInstance = Activator.CreateInstance(compilerType);

            DebugMessage("CoreCLREmbedding::CompileFunc (CLR) - Starting compilation");
            Func<object, Task<object>> compiledFunction = (Func<object, Task<object>>)compileMethod.Invoke(compilerInstance, new object[]
            {
                options,
                LoadContext.CompileAssemblies
            });
            DebugMessage("CoreCLREmbedding::CompileFunc (CLR) - Compilation complete");

            GCHandle handle = GCHandle.Alloc(compiledFunction);

            return GCHandle.ToIntPtr(handle);
        }

        catch (TargetInvocationException e)
        {
            DebugMessage("CoreCLREmbedding::CompileFunc (CLR) - Exception was thrown: {0}\n{1}", e.InnerException.Message, e.InnerException.StackTrace);

            V8Type v8Type;
            Marshal.WriteIntPtr(exception, MarshalCLRToV8(e, out v8Type));

            return IntPtr.Zero;
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::CompileFunc (CLR) - Exception was thrown: {0}\n{1}", e.Message, e.StackTrace);

            V8Type v8Type;
            Marshal.WriteIntPtr(exception, MarshalCLRToV8(e, out v8Type));

            return IntPtr.Zero;
        }
    }

    [SecurityCritical]
    public static void FreeHandle(IntPtr gcHandle)
    {
        GCHandle actualHandle = GCHandle.FromIntPtr(gcHandle);
        actualHandle.Free();
    }

    [SecurityCritical]
    public static void CallFunc(IntPtr function, IntPtr payload, int payloadType, IntPtr taskState, IntPtr result, IntPtr resultType)
    {
        try
        {
            DebugMessage("CoreCLREmbedding::CallFunc (CLR) - Starting");

            GCHandle wrapperHandle = GCHandle.FromIntPtr(function);
            Func<object, Task<object>> wrapperFunc = (Func<object, Task<object>>)wrapperHandle.Target;

            DebugMessage("CoreCLREmbedding::CallFunc (CLR) - Marshalling data of type {0} and calling the .NET method", ((V8Type)payloadType).ToString("G"));
            Task<Object> functionTask = wrapperFunc(MarshalV8ToCLR(payload, (V8Type)payloadType));

            if (functionTask.IsFaulted)
            {
                DebugMessage("CoreCLREmbedding::CallFunc (CLR) - .NET method ran synchronously and faulted, marshalling exception data for V8");

                V8Type taskExceptionType;

                Marshal.WriteInt32(taskState, (int)TaskStatus.Faulted);
                Marshal.WriteIntPtr(result, MarshalCLRToV8(functionTask.Exception, out taskExceptionType));
                Marshal.WriteInt32(resultType, (int)V8Type.Exception);
            }

            else if (functionTask.IsCompleted)
            {
                DebugMessage("CoreCLREmbedding::CallFunc (CLR) - .NET method ran synchronously, marshalling data for V8");

                V8Type taskResultType;
                IntPtr marshalData = MarshalCLRToV8(functionTask.Result, out taskResultType);

                DebugMessage("CoreCLREmbedding::CallFunc (CLR) - Method return data is of type {0}", taskResultType.ToString("G"));

                Marshal.WriteInt32(taskState, (int)TaskStatus.RanToCompletion);
                Marshal.WriteIntPtr(result, marshalData);
                Marshal.WriteInt32(resultType, (int)taskResultType);
            }

            else
            {
                DebugMessage("CoreCLREmbedding::CallFunc (CLR) - .NET method ran asynchronously, returning task handle and status");

                GCHandle taskHandle = GCHandle.Alloc(functionTask);

                Marshal.WriteInt32(taskState, (int)functionTask.Status);
                Marshal.WriteIntPtr(result, GCHandle.ToIntPtr(taskHandle));
                Marshal.WriteInt32(resultType, (int)V8Type.Task);
            }

            DebugMessage("CoreCLREmbedding::CallFunc (CLR) - Finished");
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::CallFunc (CLR) - Exception was thrown: {0}", e.Message);

            V8Type v8Type;

            Marshal.WriteIntPtr(result, MarshalCLRToV8(e, out v8Type));
            Marshal.WriteInt32(resultType, (int)v8Type);
            Marshal.WriteInt32(taskState, (int)TaskStatus.Faulted);
        }
    }

    private static void TaskCompleted(Task<object> task, object state)
    {
        DebugMessage("CoreCLREmbedding::TaskCompleted (CLR) - Task completed with a state of {0}", task.Status.ToString("G"));
        DebugMessage("CoreCLREmbedding::TaskCompleted (CLR) - Marshalling data to return to V8", task.Status.ToString("G"));

        V8Type v8Type;
        TaskState actualState = (TaskState)state;
        IntPtr resultObject;
        TaskStatus taskStatus;

        if (task.IsFaulted)
        {
            taskStatus = TaskStatus.Faulted;

            try
            {
                resultObject = MarshalCLRToV8(task.Exception, out v8Type);
            }

            catch (Exception e)
            {
                taskStatus = TaskStatus.Faulted;
                resultObject = MarshalCLRToV8(e, out v8Type);
            }
        }

        else
        {
            taskStatus = TaskStatus.RanToCompletion;

            try
            {
                resultObject = MarshalCLRToV8(task.Result, out v8Type);
            }

            catch (Exception e)
            {
                taskStatus = TaskStatus.Faulted;
                resultObject = MarshalCLRToV8(e, out v8Type);
            }
        }

        DebugMessage("CoreCLREmbedding::TaskCompleted (CLR) - Invoking unmanaged callback");
        actualState.Callback(resultObject, (int)v8Type, (int)taskStatus, actualState.Context);
    }

    [SecurityCritical]
    public static void ContinueTask(IntPtr task, IntPtr context, IntPtr callback, IntPtr exception)
    {
        try
        {
            Marshal.WriteIntPtr(exception, IntPtr.Zero);

            DebugMessage("CoreCLREmbedding::ContinueTask (CLR) - Starting");

            GCHandle taskHandle = GCHandle.FromIntPtr(task);
            Task<Object> actualTask = (Task<Object>)taskHandle.Target;

            TaskCompleteDelegate taskCompleteDelegate = Marshal.GetDelegateForFunctionPointer<TaskCompleteDelegate>(callback);
            DebugMessage("CoreCLREmbedding::ContinueTask (CLR) - Marshalled unmanaged callback successfully");

            actualTask.ContinueWith(TaskCompleted, new TaskState(taskCompleteDelegate, context));

            DebugMessage("CoreCLREmbedding::ContinueTask (CLR) - Finished");
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::ContinueTask (CLR) - Exception was thrown: {0}", e.Message);

            V8Type v8Type;
            Marshal.WriteIntPtr(exception, MarshalCLRToV8(e, out v8Type));
        }
    }

    [SecurityCritical]
    public static void SetCallV8FunctionDelegate(IntPtr callV8Function, IntPtr exception)
    {
        try
        {
            Marshal.WriteIntPtr(exception, IntPtr.Zero);
            NodejsFuncInvokeContext.CallV8Function = Marshal.GetDelegateForFunctionPointer<CallV8FunctionDelegate>(callV8Function);
        }

        catch (Exception e)
        {
            DebugMessage("CoreCLREmbedding::SetCallV8FunctionDelegate (CLR) - Exception was thrown: {0}", e.Message);

            V8Type v8Type;
            Marshal.WriteIntPtr(exception, MarshalCLRToV8(e, out v8Type));
        }
    }

    [SecurityCritical]
    public static void FreeMarshalData(IntPtr marshalData, int v8Type)
    {
        switch ((V8Type)v8Type)
        {
            case V8Type.String:
            case V8Type.Int32:
            case V8Type.Boolean:
            case V8Type.Number:
            case V8Type.Date:
                Marshal.FreeHGlobal(marshalData);
                break;

            case V8Type.Object:
            case V8Type.Exception:
                V8ObjectData objectData = Marshal.PtrToStructure<V8ObjectData>(marshalData);

                for (int i = 0; i < objectData.propertiesCount; i++)
                {
                    int propertyType = Marshal.ReadInt32(objectData.propertyTypes, i * sizeof(int));
                    IntPtr propertyValue = Marshal.ReadIntPtr(objectData.propertyValues, i * PointerSize);
                    IntPtr propertyName = Marshal.ReadIntPtr(objectData.propertyNames, i * PointerSize);

                    FreeMarshalData(propertyValue, propertyType);
                    Marshal.FreeHGlobal(propertyName);
                }

                Marshal.FreeHGlobal(objectData.propertyTypes);
                Marshal.FreeHGlobal(objectData.propertyValues);
                Marshal.FreeHGlobal(objectData.propertyNames);
                Marshal.FreeHGlobal(marshalData);

                break;

            case V8Type.Array:
                V8ArrayData arrayData = Marshal.PtrToStructure<V8ArrayData>(marshalData);

                for (int i = 0; i < arrayData.arrayLength; i++)
                {
                    int itemType = Marshal.ReadInt32(arrayData.itemTypes, i * sizeof(int));
                    IntPtr itemValue = Marshal.ReadIntPtr(arrayData.itemValues, i * PointerSize);

                    FreeMarshalData(itemValue, itemType);
                }

                Marshal.FreeHGlobal(arrayData.itemTypes);
                Marshal.FreeHGlobal(arrayData.itemValues);
                Marshal.FreeHGlobal(marshalData);

                break;

            case V8Type.Buffer:
                V8BufferData bufferData = Marshal.PtrToStructure<V8BufferData>(marshalData);

                Marshal.FreeHGlobal(bufferData.buffer);
                Marshal.FreeHGlobal(marshalData);

                break;

            case V8Type.Null:
            case V8Type.Function:
                break;

            default:
                throw new Exception("Unsupported marshalled data type: " + v8Type);
        }
    }

    // ReSharper disable once InconsistentNaming
    public static IntPtr MarshalCLRToV8(object clrObject, out V8Type v8Type)
    {
        if (clrObject == null)
        {
            v8Type = V8Type.Null;
            return IntPtr.Zero;
        }

        else if (clrObject is string)
        {
            v8Type = V8Type.String;
            return Marshal.StringToHGlobalAnsi((string) clrObject);
        }

        else if (clrObject is char)
        {
            v8Type = V8Type.String;
            return Marshal.StringToHGlobalAnsi(clrObject.ToString());
        }

        else if (clrObject is bool)
        {
            v8Type = V8Type.Boolean;
            IntPtr memoryLocation = Marshal.AllocHGlobal(sizeof (int));

            Marshal.WriteInt32(memoryLocation, ((bool) clrObject)
                ? 1
                : 0);
            return memoryLocation;
        }

        else if (clrObject is Guid)
        {
            v8Type = V8Type.String;
            return Marshal.StringToHGlobalAnsi(clrObject.ToString());
        }

        else if (clrObject is DateTime)
        {
            v8Type = V8Type.Date;
            DateTime dateTime = (DateTime) clrObject;

            if (dateTime.Kind == DateTimeKind.Local)
            {
                dateTime = dateTime.ToUniversalTime();
            }

            else if (dateTime.Kind == DateTimeKind.Unspecified)
            {
                dateTime = new DateTime(dateTime.Ticks, DateTimeKind.Utc);
            }

            long ticks = (dateTime.Ticks - MinDateTimeTicks)/10000;
            IntPtr memoryLocation = Marshal.AllocHGlobal(sizeof (double));

            WriteDouble(memoryLocation, ticks);
            return memoryLocation;
        }

        else if (clrObject is DateTimeOffset)
        {
            v8Type = V8Type.String;
            return Marshal.StringToHGlobalAnsi(clrObject.ToString());
        }

        else if (clrObject is Uri)
        {
            v8Type = V8Type.String;
            return Marshal.StringToHGlobalAnsi(clrObject.ToString());
        }

        else if (clrObject is short)
        {
            v8Type = V8Type.Int32;
            IntPtr memoryLocation = Marshal.AllocHGlobal(sizeof (int));

            Marshal.WriteInt32(memoryLocation, Convert.ToInt32(clrObject));
            return memoryLocation;
        }

        else if (clrObject is int)
        {
            v8Type = V8Type.Int32;
            IntPtr memoryLocation = Marshal.AllocHGlobal(sizeof (int));

            Marshal.WriteInt32(memoryLocation, (int) clrObject);
            return memoryLocation;
        }

        else if (clrObject is long)
        {
            v8Type = V8Type.Number;
            IntPtr memoryLocation = Marshal.AllocHGlobal(sizeof (double));

            WriteDouble(memoryLocation, Convert.ToDouble((long) clrObject));
            return memoryLocation;
        }

        else if (clrObject is double)
        {
            v8Type = V8Type.Number;
            IntPtr memoryLocation = Marshal.AllocHGlobal(sizeof (double));

            WriteDouble(memoryLocation, (double) clrObject);
            return memoryLocation;
        }

        else if (clrObject is float)
        {
            v8Type = V8Type.Number;
            IntPtr memoryLocation = Marshal.AllocHGlobal(sizeof (double));

            WriteDouble(memoryLocation, Convert.ToDouble((Single) clrObject));
            return memoryLocation;
        }

        else if (clrObject is decimal)
        {
            v8Type = V8Type.String;
            return Marshal.StringToHGlobalAnsi(clrObject.ToString());
        }

        else if (clrObject is Enum)
        {
            v8Type = V8Type.String;
            return Marshal.StringToHGlobalAnsi(clrObject.ToString());
        }

        else if (clrObject is byte[] || clrObject is IEnumerable<byte>)
        {
            v8Type = V8Type.Buffer;

            V8BufferData bufferData = new V8BufferData();
            byte[] buffer;

            if (clrObject is byte[])
            {
                buffer = (byte[]) clrObject;
            }

            else
            {
                buffer = ((IEnumerable<byte>) clrObject).ToArray();
            }

            bufferData.bufferLength = buffer.Length;
            bufferData.buffer = Marshal.AllocHGlobal(buffer.Length*sizeof (byte));

            Marshal.Copy(buffer, 0, bufferData.buffer, bufferData.bufferLength);

            IntPtr destinationPointer = Marshal.AllocHGlobal(V8BufferDataSize);
            Marshal.StructureToPtr(bufferData, destinationPointer, false);

            return destinationPointer;
        }

        else if (clrObject is IDictionary || clrObject is ExpandoObject)
        {
            v8Type = V8Type.Object;

            IEnumerable keys;
            int keyCount;
            Func<object, object> getValue;

            if (clrObject is ExpandoObject)
            {
                IDictionary<string, object> objectDictionary = (IDictionary<string, object>) clrObject;

                keys = objectDictionary.Keys;
                keyCount = objectDictionary.Keys.Count;
                getValue = index => objectDictionary[index.ToString()];
            }

            else
            {
                IDictionary objectDictionary = (IDictionary) clrObject;

                keys = objectDictionary.Keys;
                keyCount = objectDictionary.Keys.Count;
                getValue = index => objectDictionary[index];
            }

            V8ObjectData objectData = new V8ObjectData();
            int counter = 0;

            objectData.propertiesCount = keyCount;
            objectData.propertyNames = Marshal.AllocHGlobal(PointerSize*keyCount);
            objectData.propertyTypes = Marshal.AllocHGlobal(sizeof (int)*keyCount);
            objectData.propertyValues = Marshal.AllocHGlobal(PointerSize*keyCount);

            foreach (object key in keys)
            {
                Marshal.WriteIntPtr(objectData.propertyNames, counter*PointerSize, Marshal.StringToHGlobalAnsi(key.ToString()));
                V8Type propertyType;
                Marshal.WriteIntPtr(objectData.propertyValues, counter*PointerSize, MarshalCLRToV8(getValue(key), out propertyType));
                Marshal.WriteInt32(objectData.propertyTypes, counter*sizeof (int), (int) propertyType);

                counter++;
            }

            IntPtr destinationPointer = Marshal.AllocHGlobal(V8ObjectDataSize);
            Marshal.StructureToPtr(objectData, destinationPointer, false);

            return destinationPointer;
        }

        else if (clrObject is IEnumerable)
        {
            v8Type = V8Type.Array;

            V8ArrayData arrayData = new V8ArrayData();
            List<IntPtr> itemValues = new List<IntPtr>();
            List<int> itemTypes = new List<int>();

            foreach (object item in (IEnumerable) clrObject)
            {
                V8Type itemType;

                itemValues.Add(MarshalCLRToV8(item, out itemType));
                itemTypes.Add((int) itemType);
            }

            arrayData.arrayLength = itemValues.Count;
            arrayData.itemTypes = Marshal.AllocHGlobal(sizeof (int)*arrayData.arrayLength);
            arrayData.itemValues = Marshal.AllocHGlobal(PointerSize*arrayData.arrayLength);

            Marshal.Copy(itemTypes.ToArray(), 0, arrayData.itemTypes, arrayData.arrayLength);
            Marshal.Copy(itemValues.ToArray(), 0, arrayData.itemValues, arrayData.arrayLength);

            IntPtr destinationPointer = Marshal.AllocHGlobal(V8ArrayDataSize);
            Marshal.StructureToPtr(arrayData, destinationPointer, false);

            return destinationPointer;
        }

        else if (clrObject.GetType().GetTypeInfo().IsGenericType && clrObject.GetType().GetGenericTypeDefinition() == typeof (Func<,>))
        {
            Func<object, Task<object>> funcObject = clrObject as Func<object, Task<object>>;

            if (funcObject == null)
            {
                throw new Exception("Properties that return Func<> instances must return Func<object, Task<object>> instances");
            }

            v8Type = V8Type.Function;
            return GCHandle.ToIntPtr(GCHandle.Alloc(funcObject));
        }

        else
        {
            v8Type = clrObject is Exception
                ? V8Type.Exception
                : V8Type.Object;

            if (clrObject is Exception)
            {
                AggregateException aggregateException = clrObject as AggregateException;

                if (aggregateException?.InnerExceptions != null && aggregateException.InnerExceptions.Count > 0)
                {
                    clrObject = aggregateException.InnerExceptions[0];
                }

                else
                {
                    TargetInvocationException targetInvocationException = clrObject as TargetInvocationException;

                    if (targetInvocationException?.InnerException != null)
                    {
                        clrObject = targetInvocationException.InnerException;
                    }
                }
            }

            List<Tuple<string, Func<object, object>>> propertyAccessors = GetPropertyAccessors(clrObject.GetType());
            V8ObjectData objectData = new V8ObjectData();
            int counter = 0;

            objectData.propertiesCount = propertyAccessors.Count;
            objectData.propertyNames = Marshal.AllocHGlobal(PointerSize*propertyAccessors.Count);
            objectData.propertyTypes = Marshal.AllocHGlobal(sizeof (int)*propertyAccessors.Count);
            objectData.propertyValues = Marshal.AllocHGlobal(PointerSize*propertyAccessors.Count);

            foreach (Tuple<string, Func<object, object>> propertyAccessor in propertyAccessors)
            {
                Marshal.WriteIntPtr(objectData.propertyNames, counter*PointerSize, Marshal.StringToHGlobalAnsi(propertyAccessor.Item1));

                V8Type propertyType;

                Marshal.WriteIntPtr(objectData.propertyValues, counter*PointerSize, MarshalCLRToV8(propertyAccessor.Item2(clrObject), out propertyType));
                Marshal.WriteInt32(objectData.propertyTypes, counter*sizeof (int), (int) propertyType);
                counter++;
            }

            IntPtr destinationPointer = Marshal.AllocHGlobal(V8ObjectDataSize);
            Marshal.StructureToPtr(objectData, destinationPointer, false);

            return destinationPointer;
        }
    }

    public static object MarshalV8ToCLR(IntPtr v8Object, V8Type objectType)
    {
        switch (objectType)
        {
            case V8Type.String:
                return Marshal.PtrToStringAnsi(v8Object);

            case V8Type.Object:
                return V8ObjectToExpando(Marshal.PtrToStructure<V8ObjectData>(v8Object));

            case V8Type.Boolean:
                return Marshal.ReadByte(v8Object) != 0;

            case V8Type.Number:
                return ReadDouble(v8Object);

            case V8Type.Date:
                double ticks = ReadDouble(v8Object);
                return new DateTime(Convert.ToInt64(ticks) * 10000 + MinDateTimeTicks, DateTimeKind.Utc);

            case V8Type.Null:
                return null;

            case V8Type.Int32:
                return Marshal.ReadInt32(v8Object);

            case V8Type.UInt32:
                return (uint)Marshal.ReadInt32(v8Object);

            case V8Type.Function:
                NodejsFunc nodejsFunc = new NodejsFunc(v8Object);
                return nodejsFunc.GetFunc();

            case V8Type.Array:
                V8ArrayData arrayData = Marshal.PtrToStructure<V8ArrayData>(v8Object);
                object[] array = new object[arrayData.arrayLength];

                for (int i = 0; i < arrayData.arrayLength; i++)
                {
                    int itemType = Marshal.ReadInt32(arrayData.itemTypes, i * sizeof(int));
                    IntPtr itemValuePointer = Marshal.ReadIntPtr(arrayData.itemValues, i * PointerSize);

                    array[i] = MarshalV8ToCLR(itemValuePointer, (V8Type)itemType);
                }

                return array;

            case V8Type.Buffer:
                V8BufferData bufferData = Marshal.PtrToStructure<V8BufferData>(v8Object);
                byte[] buffer = new byte[bufferData.bufferLength];

                Marshal.Copy(bufferData.buffer, buffer, 0, bufferData.bufferLength);

                return buffer;

            case V8Type.Exception:
                string message = Marshal.PtrToStringAnsi(v8Object);
                return new Exception(message);

            default:
                throw new Exception("Unsupported V8 object type: " + objectType + ".");
        }
    }

    private static unsafe void WriteDouble(IntPtr pointer, double value)
    {
        try
        {
            byte* address = (byte*)pointer;

            if ((unchecked((int)address) & 0x7) == 0)
            {
                *((double*)address) = value;
            }

            else
            {
                byte* valuePointer = (byte*)&value;

                address[0] = valuePointer[0];
                address[1] = valuePointer[1];
                address[2] = valuePointer[2];
                address[3] = valuePointer[3];
                address[4] = valuePointer[4];
                address[5] = valuePointer[5];
                address[6] = valuePointer[6];
                address[7] = valuePointer[7];
            }
        }

        catch (NullReferenceException)
        {
            throw new Exception("Access violation.");
        }
    }

    private static unsafe double ReadDouble(IntPtr pointer)
    {
        try
        {
            byte* address = (byte*)pointer;

            if ((unchecked((int)address) & 0x7) == 0)
            {
                return *((double*)address);
            }

            else
            {
                double value;
                byte* valuePointer = (byte*)&value;

                valuePointer[0] = address[0];
                valuePointer[1] = address[1];
                valuePointer[2] = address[2];
                valuePointer[3] = address[3];
                valuePointer[4] = address[4];
                valuePointer[5] = address[5];
                valuePointer[6] = address[6];
                valuePointer[7] = address[7];

                return value;
            }
        }

        catch (NullReferenceException)
        {
            throw new Exception("Access violation.");
        }
    }

    private static ExpandoObject V8ObjectToExpando(V8ObjectData v8Object)
    {
        ExpandoObject expando = new ExpandoObject();
        IDictionary<string, object> expandoDictionary = expando;

        for (int i = 0; i < v8Object.propertiesCount; i++)
        {
            int propertyType = Marshal.ReadInt32(v8Object.propertyTypes, i * sizeof(int));
            IntPtr propertyNamePointer = Marshal.ReadIntPtr(v8Object.propertyNames, i * PointerSize);
            string propertyName = Marshal.PtrToStringAnsi(propertyNamePointer);
            IntPtr propertyValuePointer = Marshal.ReadIntPtr(v8Object.propertyValues, i * PointerSize);

            expandoDictionary.Add(propertyName, MarshalV8ToCLR(propertyValuePointer, (V8Type)propertyType));
        }

        return expando;
    }

    [Conditional("DEBUG")]
    internal static void DebugMessage(string message, params object[] parameters)
    {
        if (DebugMode)
        {
            Console.WriteLine(message, parameters);
        }
    }

    private static List<Tuple<string, Func<object, object>>> GetPropertyAccessors(Type type)
    {
        if (TypePropertyAccessors.ContainsKey(type))
        {
            return TypePropertyAccessors[type];
        }

        List<Tuple<string, Func<object, object>>> propertyAccessors = new List<Tuple<string, Func<object, object>>>();

        foreach (PropertyInfo propertyInfo in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            ParameterExpression instance = Expression.Parameter(typeof(object));
            UnaryExpression instanceConvert = Expression.TypeAs(instance, type);
            MemberExpression property = Expression.Property(instanceConvert, propertyInfo);
            UnaryExpression propertyConvert = Expression.TypeAs(property, typeof(object));

            propertyAccessors.Add(new Tuple<string, Func<object, object>>(propertyInfo.Name, (Func<object, object>)Expression.Lambda(propertyConvert, instance).Compile()));
        }

        foreach (FieldInfo fieldInfo in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            ParameterExpression instance = Expression.Parameter(typeof(object));
            UnaryExpression instanceConvert = Expression.TypeAs(instance, type);
            MemberExpression field = Expression.Field(instanceConvert, fieldInfo);
            UnaryExpression fieldConvert = Expression.TypeAs(field, typeof(object));

            propertyAccessors.Add(new Tuple<string, Func<object, object>>(fieldInfo.Name, (Func<object, object>)Expression.Lambda(fieldConvert, instance).Compile()));
        }

        if (typeof(Exception).IsAssignableFrom(type) && !propertyAccessors.Any(a => a.Item1 == "Name"))
        {
            propertyAccessors.Add(new Tuple<string, Func<object, object>>("Name", o => type.FullName));
        }

        TypePropertyAccessors[type] = propertyAccessors;

        return propertyAccessors;
    }
}
