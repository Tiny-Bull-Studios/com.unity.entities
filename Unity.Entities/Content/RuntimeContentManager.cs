#if !UNITY_DOTSRUNTIME
//#define ENABLE_CONTENT_DIAGNOSTICS
using System;
using UnityEngine;
using Unity.Loading;
using Unity.Collections;
using Unity.IO.Archive;
using Unity.Entities.Serialization;
using Unity.Collections.LowLevel.Unsafe;
using System.Runtime.InteropServices;
using UnityEngine.SceneManagement;
using Unity.Content;
using Unity.Burst;
#if ENABLE_PROFILER
using Unity.Profiling;
using System.Runtime.CompilerServices;
using Unity.Profiling.LowLevel.Unsafe;
#endif

namespace Unity.Entities.Content
{
    /// <summary>
    /// Class that manages resources loaded from content archives.
    /// </summary>
    [GenerateTestsForBurstCompatibility]
    [BurstCompile]
    public static class RuntimeContentManager
    {
        internal const string k_NameSpaceString = "RTC";
        internal const string k_ContentArchiveDirectory = "ContentArchives";
        internal const string k_ContentCatalogFilename = "archive_dependencies.bin";
        static string ArchivePrefix;
        internal static ContentNamespace Namespace;

        /// <summary>
        /// The relative path of the catalog.  
        /// </summary>
        [ExcludeFromBurstCompatTesting("References managed objects")]
        public static string RelativeCatalogPath => $"{k_ContentArchiveDirectory}/{k_ContentCatalogFilename}";
        /// <summary>
        /// Functor that transforms a file id into the internal mounted path.
        /// </summary>
        /// <param name="fileId">The file id.</param>
        /// <returns>The internal mount path for the file. This is implemented as $"ns:/{k_NameSpaceString}/{ArchivePrefix}/{fileId}"</returns>
        [ExcludeFromBurstCompatTesting("References managed objects")]
        public static string DefaultContentFileNameFunc(string fileId) => $"ns:/{k_NameSpaceString}/{ArchivePrefix}/{fileId}";
        /// <summary>
        /// Functor that transforms an archive id into a relative path.  This path should be relative to the streaming assets path.
        /// </summary>
        /// <param name="archiveId">The archive id.</param>
        /// <returns>The relative path of the archive file.</returns>
        [ExcludeFromBurstCompatTesting("References managed objects")]
        public static string DefaultArchivePathFunc(string archiveId) => $"{k_ContentArchiveDirectory}/{archiveId}";

        struct ActiveArchive
        {
            public ContentArchiveId ArchiveId;
            public int ReferenceCount;
            public ArchiveHandle Archive;
            public override int GetHashCode() => ArchiveId.GetHashCode();
        }

        struct ActiveFile
        {
            public ContentFileId ContentFileId;
            public int ReferenceCount;
            public ContentArchiveId ArchiveId;
            public ContentFile File;
            public override int GetHashCode() => ContentFileId.GetHashCode();
        }

        unsafe struct ActiveObject
        {
            public UntypedWeakReferenceId ObjectReferenceId;
            public int ReferenceCount;
            public ContentFileId ContentFileId;
            public long LocalIdentifierInFile;
            public override int GetHashCode() => ObjectReferenceId.GetHashCode();
        }

        struct ActiveScene
        {
            public UntypedWeakReferenceId SceneId;
            public ContentSceneFile SceneFile;
            public ContentFileId FileId;
            public int ReferenceCount;
            public ContentArchiveId ArchiveId;
            public override int GetHashCode() => SceneId.GetHashCode();
        }

        struct ActiveDependencySet
        {
            public int ReferenceCount;
            public UnsafeList<ContentFile> Files;
        }

#if ENABLE_CONTENT_DIAGNOSTICS
        /// <summary>
        /// Functor that handles log events.  This can be overriden by the end user if needed.
        /// </summary>
        public static Action<string> LogFunc;
#endif


        //shared static data between C# & HPC#
        static readonly SharedStatic<ObjectValueCache> SharedStaticObjectValueCache = SharedStatic<ObjectValueCache>.GetOrCreate<ObjectValueCacheType>();
        struct ObjectValueCacheType { }
        static readonly SharedStatic<MultiProducerSingleBulkConsumerQueue<UntypedWeakReferenceId>> SharedStaticObjectLoadQueue = SharedStatic<MultiProducerSingleBulkConsumerQueue<UntypedWeakReferenceId>>.GetOrCreate<LoadQueueType>();
        struct LoadQueueType { }
        static readonly SharedStatic<MultiProducerSingleBulkConsumerQueue<UntypedWeakReferenceId>> SharedStaticObjectReleaseQueue = SharedStatic<MultiProducerSingleBulkConsumerQueue<UntypedWeakReferenceId>>.GetOrCreate<ReleaseQueueType>();
        struct ReleaseQueueType { }
        static readonly SharedStatic<UnsafeList<UntypedWeakReferenceId>> SharedStaticLoadingObjects = SharedStatic<UnsafeList<UntypedWeakReferenceId>>.GetOrCreate<LoadingObjectsType>();
        struct LoadingObjectsType { }

        static UnsafeHashMap<ContentArchiveId, ActiveArchive> ActiveArchives;
        static UnsafeHashMap<ContentFileId, ActiveFile> ActiveFiles;
        static UnsafeHashMap<UntypedWeakReferenceId, ActiveObject> ActiveObjects;
        static RuntimeContentCatalog Catalog;
        static UnsafeList<ActiveDependencySet> ActiveDependencySets;
        static UnsafeHashMap<UntypedWeakReferenceId, ActiveScene> ActiveScenes;
        static int currentGeneration = -1;

        /// <summary>
        /// Initialize the internal data structures for handling content archives.
        /// </summary>
        [ExcludeFromBurstCompatTesting("References managed objects")]
        public unsafe static void Initialize()
        {
#if ENABLE_PROFILER
            RuntimeContentManagerProfiler.Initialize();
#endif

            if (ActiveObjects.IsCreated)
            {
                Debug.LogWarning("Initialize called before Cleanup!");
                Cleanup(out var _);
            }
             
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc = s => Debug.Log(s);
#endif
            currentGeneration++;
            Namespace = ContentNamespace.GetOrCreateNamespace(k_NameSpaceString);
            ArchivePrefix = $"a{currentGeneration}:";
            Catalog = new RuntimeContentCatalog();
#if UNITY_EDITOR
            Catalog.Initialize();
#endif
            ActiveArchives = new UnsafeHashMap<ContentArchiveId, ActiveArchive>(8, Allocator.Persistent);
            ActiveFiles = new UnsafeHashMap<ContentFileId, ActiveFile>(8, Allocator.Persistent);
            ActiveObjects = new UnsafeHashMap<UntypedWeakReferenceId, ActiveObject>(8, Allocator.Persistent);
            ActiveScenes = new UnsafeHashMap<UntypedWeakReferenceId, ActiveScene>(8, Allocator.Persistent);

            SharedStaticObjectValueCache.Data = new ObjectValueCache(8);
            SharedStaticObjectLoadQueue.Data = new MultiProducerSingleBulkConsumerQueue<UntypedWeakReferenceId>(8);
            SharedStaticObjectReleaseQueue.Data = new MultiProducerSingleBulkConsumerQueue<UntypedWeakReferenceId>(8);
            SharedStaticLoadingObjects.Data = new UnsafeList<UntypedWeakReferenceId>(8, Allocator.Persistent);

            if (s_LoadObjectDelegateGCPrevention == null)
            {
                var trampoline = new LoadObjectManagedDelegate(LoadObjectsImpl);
                s_LoadObjectDelegateGCPrevention = trampoline; // Need to hold on to this
                s_ManagedLoadObjectTrampoline.Data = Marshal.GetFunctionPointerForDelegate(trampoline);
            }

            if (s_ReleaseObjectDelegateGCPrevention == null)
            {
                var trampoline = new ReleaseObjectManagedDelegate(ReleaseObjectsImpl);
                s_ReleaseObjectDelegateGCPrevention = trampoline; // Need to hold on to this
                s_ManagedReleaseObjectTrampoline.Data = Marshal.GetFunctionPointerForDelegate(trampoline);
            }

            if (s_ProcessObjectDelegateGCPrevention == null)
            {
                var trampoline = new ProcessObjectManagedDelegate(UpdateLoadingObjectStatus);
                s_ProcessObjectDelegateGCPrevention = trampoline; // Need to hold on to this
                s_ManagedProcessObjectTrampoline.Data = Marshal.GetFunctionPointerForDelegate(trampoline);
            }

#if ENABLE_PROFILER
            if (s_SendProfilerFrameDataManagedDelegateGCPrevention == null)
            {
                var trampoline = new SendProfilerFrameDataManagedDelegate(SendProfilerFrameData);
                s_SendProfilerFrameDataManagedDelegateGCPrevention = trampoline; // Need to hold on to this
                s_SendProfilerFrameDataManagedTrampoline.Data = Marshal.GetFunctionPointerForDelegate(trampoline);
            }
#endif
        }

        /// <summary>
        /// Cleanup internal resources.
        /// </summary>
        /// <param name="unreleasedObjectCount">The number of unreleased objects found during cleanup.</param>
        /// <returns>True if no unreleased objects were found, false otherwise.</returns>
        [ExcludeFromBurstCompatTesting("References managed objects")]
        public unsafe static bool Cleanup(out int unreleasedObjectCount)
        {
            unreleasedObjectCount = 0;
            if (!ActiveObjects.IsCreated)
                return true;
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Cleanup Called, active objects: {(ActiveObjects.IsCreated ? ActiveObjects.Count : 0)}");
#endif
            try
            {
                SharedStaticObjectLoadQueue.Data.Dispose();
                SharedStaticObjectReleaseQueue.Data.Dispose();
                SharedStaticLoadingObjects.Data.Dispose();
                if (ActiveObjects.IsCreated && ActiveObjects.Count > 0)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    LogFunc?.Invoke($"Unreleased objects found on exit, cleaning up {ActiveObjects.Count} items.");
#endif
                    using (var keys = ActiveObjects.GetKeyArray(Allocator.Temp))
                    {
                        for (int i = 0; i < keys.Length; i++)
                        {
                            unreleasedObjectCount++;
#if ENABLE_CONTENT_DIAGNOSTICS
                            LogFunc?.Invoke($"Releasing object {keys[i]}");
#endif
                            while (ActiveObjects.TryGetValue(keys[i], out var activeObject))
                                ReleaseObjectImpl(activeObject.ObjectReferenceId);
                        }
                    }
                }
                if (Catalog.IsCreated)
                    Catalog.Dispose();

                if (ActiveScenes.IsCreated && ActiveScenes.Count > 0)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    LogFunc?.Invoke($"Unreleased scenes found on exit, cleaning up {ActiveScenes.Count} items.");
#endif
                    using (var keys = ActiveScenes.GetKeyArray(Allocator.Temp))
                    {
                        for (int i = 0; i < keys.Length; i++)
                        {
#if ENABLE_CONTENT_DIAGNOSTICS
                            LogFunc?.Invoke($"Releasing scene {keys[i]}");
#endif
                            while (ActiveScenes.TryGetValue(keys[i], out var activeScene))
                                ReleaseScene(activeScene.SceneId);
                        }
                    }
                }
                if (ActiveScenes.IsCreated)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    if (ActiveScenes.Count > 0)
                        LogFunc?.Invoke($"Cleanup: ActiveScenes contains {ActiveScenes.Count} items.");
#endif
                    ActiveScenes.Dispose();
                }

                if (ActiveArchives.IsCreated)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    if (ActiveArchives.Count > 0)
                        LogFunc?.Invoke($"Cleanup: ActiveArchives contains {ActiveArchives.Count} items.");
#endif
                    ActiveArchives.Dispose();
                }
                if (ActiveDependencySets.IsCreated)
                {
                    for (int i = 0; i < ActiveDependencySets.Length; i++)
                    {
                        var ds = ActiveDependencySets[i];
                        if (ds.Files.IsCreated)
                        {
#if ENABLE_CONTENT_DIAGNOSTICS
                            LogFunc?.Invoke($"Cleanup: ActiveDependencySets[{i}] contains {ds.Files.Length} items, ref count {ds.ReferenceCount}");
#endif
                            ds.Files.Dispose();
                        }
                    }
                    ActiveDependencySets.Dispose();
                }
                if (ActiveFiles.IsCreated)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    if (ActiveFiles.Count > 0)
                        LogFunc?.Invoke($"Cleanup: ActiveFiles contains {ActiveFiles.Count} items.");
#endif
                    ActiveFiles.Dispose();
                }
                if (ActiveObjects.IsCreated)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    if (ActiveObjects.Count > 0)
                        LogFunc?.Invoke($"Cleanup: ActiveObjects contains {ActiveObjects.Count} items.");
#endif
                    ActiveObjects.Dispose();
                }

                if (SharedStaticObjectValueCache.Data.IsCreated)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    if (SharedStaticObjectValueCache.Data.Count() > 0)
                        LogFunc?.Invoke($"Cleanup: ObjectValueCache contains {SharedStaticObjectValueCache.Data.Count()} items.");
#endif
                    SharedStaticObjectValueCache.Data.Dispose();
                }
#if UNITY_EDITOR
                if (OverrideLoader != null && OverrideLoader.IsCreated)
                    OverrideLoader.Dispose();
#endif
                Resources.UnloadUnusedAssets();
#if ENABLE_PROFILER
                RuntimeContentManagerProfiler.Cleanup();
                if (profilerFrameArchiveData.IsCreated)
                    profilerFrameArchiveData.Dispose();
                if (profilerFrameFileData.IsCreated)
                    profilerFrameFileData.Dispose();
                if (profilerFrameObjectData.IsCreated)
                    profilerFrameObjectData.Dispose();
#endif
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }

            return unreleasedObjectCount == 0;
        }

#if UNITY_EDITOR
        [ExcludeFromBurstCompatTesting("References managed engine API")]
        [UnityEditor.InitializeOnLoadMethod]
        static void EditorInitializeOnLoadMethod()
        {
            Cleanup(out var _);
            Initialize();
#if USE_ARCHIVES_IN_EDITOR_PLAY_MODE
            RegisterForContentDeliverySystemCallback();
#endif
        }
#else
        [ExcludeFromBurstCompatTesting("References managed engine API")]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RuntimeInitialization()
        {
            Initialize();
#if ENABLE_CONTENT_DELIVERY
            RegisterForContentDeliverySystemCallback();
#endif
        }
#endif

#if ENABLE_CONTENT_DELIVERY
        static void RegisterForContentDeliverySystemCallback()
        {
            World.SystemCreated += (w, s) =>
            {
                if (s is ContentDeliverySystem cds)
                {
                    Debug.Log($"Registering RCM");
                    cds.RegisterForContentUpdateCompletion(i =>
                    {
                        Debug.Log($"RCM callback status: {i}");
                        if (i == ContentDeliverySystem.ContentUpdateState.UsingContentFromStreamingAssets)
                        {
                            LoadLocalCatalogData(
                                $"{Application.streamingAssetsPath}/{RelativeCatalogPath}",
                                DefaultContentFileNameFunc,
                                p => $"{Application.streamingAssetsPath}/{DefaultArchivePathFunc(p)}");
                        }
                        else
                        {
                            LoadLocalCatalogData(
                                cds.PathRemapFunc(RelativeCatalogPath),
                                DefaultContentFileNameFunc,
                                p => cds.PathRemapFunc(DefaultArchivePathFunc(p)));
                        }
                    });
                }
            };
        }
#endif

        /// <summary>
        /// Loads catalog data from a local path.  
        /// </summary>
        /// <param name="catalogPath">The full path to the catalog file.</param>
        /// <param name="fileNameFunc">Functor to transform internal content file names.  The string passed in is the file id and the expected returned string is the internal archive file path.  (e.g. $"ns:/{k_NameSpaceString}/{ArchivePrefix}/{fileId}")</param>
        /// <param name="archivePathFunc">Functor to transform content archive ids to full local paths.</param>
        /// <returns>True if the data was loaded successfully.</returns>
        [ExcludeFromBurstCompatTesting("References managed objects")]
        public static bool LoadLocalCatalogData(string catalogPath, Func<string, string> fileNameFunc, Func<string, string> archivePathFunc)
        {
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"LoadLocalCatalogData({catalogPath})");
#endif
            if (!Catalog.LoadCatalogData(catalogPath, archivePathFunc, fileNameFunc))
            {
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Failed to load catalog from path {catalogPath}.");
#endif
                return false;
            }
            if (!ActiveDependencySets.IsCreated)
                ActiveDependencySets = new UnsafeList<ActiveDependencySet>(Catalog.FileDependencySetCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            ActiveDependencySets.Resize(Catalog.FileDependencySetCount, NativeArrayOptions.ClearMemory);

#if ENABLE_CONTENT_DIAGNOSTICS
            if (LogFunc != null)
                Catalog.Print(LogFunc);
#endif
            return true;
        }

        /// <summary>
        /// Get the entire list of object ids.
        /// </summary>
        /// <param name="alloc">Allocator to use for created NativeArray.</param>
        /// <returns>The set of object ids.  The caller is responsible for disposing the returned array.</returns>
        [ExcludeFromBurstCompatTesting("Cannot return structs")]
        public static NativeArray<UntypedWeakReferenceId> GetObjectIds(Allocator alloc) => Catalog.GetObjectIds(alloc);

        /// <summary>
        /// Get the entire list of object ids.
        /// </summary>
        /// <param name="alloc">Allocator to use for created NativeArray.</param>
        /// <returns>The set of object ids.  The caller is responsible for disposing the returned array.</returns>
        [ExcludeFromBurstCompatTesting("Cannot return structs")]
        public static NativeArray<UntypedWeakReferenceId> GetSceneIds(Allocator alloc) => Catalog.GetSceneIds(alloc);

        /// <summary>
        /// Thread safe method to initiate an object load. The load will start during the main thread update.
        /// </summary>
        /// <param name="objectId">The object id to load.</param>
        [BurstCompile]
        [GenerateTestsForBurstCompatibility]
        public unsafe static void LoadObjectAsync(in UntypedWeakReferenceId objectId)
        {
            SharedStaticObjectLoadQueue.Data.Produce(objectId);
            SharedStaticObjectValueCache.Data.AddEntry(objectId);
        }

        /// <summary>
        /// Thread safe method to release an object.  The release will happen during the main thread update.
        /// </summary>
        /// <param name="objectId">The object id to release.</param>
        [BurstCompile]
        [GenerateTestsForBurstCompatibility]
        public unsafe static void ReleaseObjectAsync(in UntypedWeakReferenceId objectId)
        {
            SharedStaticObjectReleaseQueue.Data.Produce(objectId);
        }

        unsafe private delegate void LoadObjectManagedDelegate(UntypedWeakReferenceId* ids, int count);
        private static readonly SharedStatic<IntPtr> s_ManagedLoadObjectTrampoline = SharedStatic<IntPtr>.GetOrCreate<LoadObjectManagedDelegate>();
        private static object s_LoadObjectDelegateGCPrevention;
        [ExcludeFromBurstCompatTesting("LoadObjectImpl is not burstable")]
        [MonoPInvokeCallback(typeof(LoadObjectManagedDelegate))]
        private unsafe static void LoadObjectsImpl(UntypedWeakReferenceId* pObjectIds, int count)
        {
            try
            {
                if (ActiveObjects.Capacity - ActiveObjects.Count < count)
                {
                    ActiveObjects.Capacity = ActiveObjects.Count + count;
                    if (SharedStaticLoadingObjects.Data.Capacity - SharedStaticLoadingObjects.Data.Length < count)
                        SharedStaticLoadingObjects.Data.Capacity = SharedStaticLoadingObjects.Data.Length + count;
                }

                for (int i = 0; i < count; i++)
                    LoadObjectImpl(pObjectIds[i]);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        unsafe private delegate void ReleaseObjectManagedDelegate(UntypedWeakReferenceId* ids, int count);
        private static readonly SharedStatic<IntPtr> s_ManagedReleaseObjectTrampoline = SharedStatic<IntPtr>.GetOrCreate<ReleaseObjectManagedDelegate>();
        private static object s_ReleaseObjectDelegateGCPrevention;
        [ExcludeFromBurstCompatTesting("ReleaseObjectImpl is not burstable")]
        [MonoPInvokeCallback(typeof(ReleaseObjectManagedDelegate))]
        private unsafe static void ReleaseObjectsImpl(UntypedWeakReferenceId* pObjectIds, int count)
        {
            try
            {
                for (int i = 0; i < count; i++)
                    ReleaseObjectImpl(pObjectIds[i]);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        unsafe private delegate void ProcessObjectManagedDelegate();
        private static readonly SharedStatic<IntPtr> s_ManagedProcessObjectTrampoline = SharedStatic<IntPtr>.GetOrCreate<ProcessObjectManagedDelegate>();
        private static object s_ProcessObjectDelegateGCPrevention;
        [ExcludeFromBurstCompatTesting("ComputeObjectLoadingStatus is not burstable")]
        [MonoPInvokeCallback(typeof(ProcessObjectManagedDelegate))]
        private unsafe static void UpdateLoadingObjectStatus()
        {
            try
            {
                var data = SharedStaticLoadingObjects.Data;
                for (int i = 0; i < data.Length;)
                {
                    var id = data[i];
                    var s = ComputeObjectLoadingStatus(id);
                    if (s >= ObjectLoadingStatus.Completed)
                    {
                        var gcHandle = GetObjectHandleImpl(id);
                        SharedStaticObjectValueCache.Data.SetObjectStatus(id, gcHandle.IsAllocated ? ObjectLoadingStatus.Completed : ObjectLoadingStatus.Error, gcHandle);
                        data.RemoveAtSwapBack(i);
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

#if ENABLE_PROFILER
        static NativeArray<RuntimeContentManagerProfilerFrameData> profilerFrameArchiveData;
        static NativeArray<RuntimeContentManagerProfilerFrameData> profilerFrameFileData;
        static NativeArray<RuntimeContentManagerProfilerFrameData> profilerFrameObjectData;

        static void PrepareArray(ref NativeArray<RuntimeContentManagerProfilerFrameData> array, int size)
        {
            //create if needed
            if (!array.IsCreated)
                array = new NativeArray<RuntimeContentManagerProfilerFrameData>(size, Allocator.Persistent);
            //dispose and recreate if size doesnt match
            if (array.Length != size)
            {
                array.Dispose();
                array = new NativeArray<RuntimeContentManagerProfilerFrameData>(size, Allocator.Persistent);
            }
        }

        unsafe private delegate void SendProfilerFrameDataManagedDelegate();
        private static readonly SharedStatic<IntPtr> s_SendProfilerFrameDataManagedTrampoline = SharedStatic<IntPtr>.GetOrCreate<SendProfilerFrameDataManagedDelegate>();
        private static object s_SendProfilerFrameDataManagedDelegateGCPrevention;
        [ExcludeFromBurstCompatTesting("SendProfilerFrameData is not burstable")]
        [MonoPInvokeCallback(typeof(SendProfilerFrameDataManagedDelegate))]
        private unsafe static void SendProfilerFrameData()
        {
            try
            {
                if (!ActiveObjects.IsCreated)
                    return;
                {
                    PrepareArray(ref profilerFrameArchiveData, ActiveArchives.Count);
                    int index = 0;
                    foreach (var o in ActiveArchives)
                        profilerFrameArchiveData[index++] = new RuntimeContentManagerProfilerFrameData {
                            id = new UntypedWeakReferenceId { GlobalId = new RuntimeGlobalObjectId { AssetGUID = o.Key.Value } }, refCount = o.Value.ReferenceCount, parent = default };
                    UnityEngine.Profiling.Profiler.EmitFrameMetaData(RuntimeContentManagerProfiler.Guid, 0, profilerFrameArchiveData);
                }

                {
                    PrepareArray(ref profilerFrameFileData, ActiveFiles.Count);
                    int index = 0;
                    foreach (var o in ActiveFiles)
                        profilerFrameFileData[index++] = new RuntimeContentManagerProfilerFrameData
                        {
                            id = new UntypedWeakReferenceId { GlobalId = new RuntimeGlobalObjectId { AssetGUID = o.Key.Value } },
                            refCount = o.Value.ReferenceCount,
                            parent = o.Value.ArchiveId.Value.GetHashCode()
                        };// new UntypedWeakReferenceId { GlobalId = new RuntimeGlobalObjectId { AssetGUID = o.Value.ArchiveId.Value } } };
                    UnityEngine.Profiling.Profiler.EmitFrameMetaData(RuntimeContentManagerProfiler.Guid, 1, profilerFrameFileData);
                }

                {
                    PrepareArray(ref profilerFrameObjectData, ActiveObjects.Count);
                    int index = 0;
                    foreach (var o in ActiveObjects)
                        profilerFrameObjectData[index++] = new RuntimeContentManagerProfilerFrameData
                        {
                            id = o.Key,
                            refCount = o.Value.ReferenceCount,
                            parent = o.Value.ContentFileId.Value.GetHashCode()
                        }; //new UntypedWeakReferenceId { GlobalId = new RuntimeGlobalObjectId { AssetGUID = o.Value.ContentFileId.Value } } };
                    UnityEngine.Profiling.Profiler.EmitFrameMetaData(RuntimeContentManagerProfiler.Guid, 2, profilerFrameObjectData);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
#endif
        /// <summary>
        /// Process queued load and release requests.
        /// </summary>
        [BurstCompile]
        [GenerateTestsForBurstCompatibility]
        public unsafe static void ProcessQueuedCommands()
        {
#if ENABLE_PROFILER
            RuntimeContentManagerProfiler.EnterProcessCommands();
#endif
            if (SharedStaticObjectLoadQueue.Data.ConsumeAll(out var objectLoads, Allocator.Temp))
            {
                new FunctionPointer<LoadObjectManagedDelegate>(s_ManagedLoadObjectTrampoline.Data).Invoke((UntypedWeakReferenceId *)objectLoads.GetUnsafePtr<UntypedWeakReferenceId>(), objectLoads.Length);
                objectLoads.Dispose();
            }

            if (SharedStaticObjectReleaseQueue.Data.ConsumeAll(out var objectReleases, Allocator.Temp))
            {
                new FunctionPointer<ReleaseObjectManagedDelegate>(s_ManagedReleaseObjectTrampoline.Data).Invoke((UntypedWeakReferenceId*)objectReleases.GetUnsafePtr<UntypedWeakReferenceId>(), objectReleases.Length);
                objectReleases.Dispose();
            }

            if(!SharedStaticLoadingObjects.Data.IsEmpty)
                new FunctionPointer<ProcessObjectManagedDelegate>(s_ManagedProcessObjectTrampoline.Data).Invoke();
#if ENABLE_PROFILER
            RuntimeContentManagerProfiler.ExitProcessCommands();
            if (UnityEngine.Profiling.Profiler.enabled)
                new FunctionPointer<SendProfilerFrameDataManagedDelegate>(s_SendProfilerFrameDataManagedTrampoline.Data).Invoke();
#endif
        }


        //used by tests
        [ExcludeFromBurstCompatTesting("ActiveObjects is static and not shared")]
        internal static int CompletedObjectLoads()
        {
            int count = 0;
            foreach (var a in ActiveObjects)
                if (GetObjectLoadingStatus(a.Key) >= ObjectLoadingStatus.Completed)
                    count++;
            return count;
        }

        //used by tests
        [ExcludeFromBurstCompatTesting("ActiveObjects is static and not shared")]
        internal unsafe static int UnfinishedObjectLoads()
        {
            int count = SharedStaticObjectLoadQueue.Data.Length;
            foreach (var a in ActiveObjects)
                if (GetObjectLoadingStatus(a.Key) < ObjectLoadingStatus.Completed)
                    count++;
            return count;
        }

        [ExcludeFromBurstCompatTesting("References static data")]
        private static void ReleaseActiveDependencySet(int depIndex, in UnsafeList<ContentFileId> deps)
        {
            var activeDepSet = ActiveDependencySets[depIndex];
            if (--activeDepSet.ReferenceCount == 0)
            {
                if (deps.Length > 0)
                {
                    for (int i = 0; i < deps.Length; i++)
                        if (deps[i].IsValid)
                            ReleaseFile(deps[i]);
                    activeDepSet.Files.Dispose();
                }
            }
            ActiveDependencySets[depIndex] = activeDepSet;
        }

        [ExcludeFromBurstCompatTesting("References static data")]
        static void LoadActiveDependencySet(int depIndex, in UnsafeList<ContentFileId> deps, out ActiveDependencySet activeDepSet)
        {
            activeDepSet = ActiveDependencySets[depIndex];
            if (activeDepSet.ReferenceCount == 0)
            {
                if (deps.Length > 0)
                {
                    activeDepSet.Files = new UnsafeList<ContentFile>(deps.Length, Allocator.Persistent);
                    for (int i = 0; i < deps.Length; i++)
                    {
                        var dep = deps[i];
                        if (dep.IsValid)
                            activeDepSet.Files.Add(LoadFile(dep).File);
                        else
                            activeDepSet.Files.Add(ContentFile.GlobalTableDependency);
                    }
                }
            }
            activeDepSet.ReferenceCount++;
            ActiveDependencySets[depIndex] = activeDepSet;
        }

        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        unsafe static ActiveFile LoadFile(ContentFileId fileId)
        {
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loading file {fileId}");
#endif
            if (!ActiveFiles.TryGetValue(fileId, out var activeFile))
            {
                if (!Catalog.TryGetFileLocation(fileId, out string filePath, out var deps, out var archiveId, out var depIndex))
                    throw new Exception($"Invalid file location: {fileId}");

                activeFile = new ActiveFile() { ContentFileId = fileId, ArchiveId = archiveId, ReferenceCount = 1 };
                LoadActiveDependencySet(depIndex, in deps, out var activeDepSet);

                var activeArchive = archiveId.IsValid ? LoadArchive(archiveId) : default;
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Starting file load for {fileId}");
#endif
                if (fileId.IsValid && !string.IsNullOrEmpty(filePath) && archiveId.IsValid)
                    activeFile.File = ContentLoadInterface.LoadContentFileAsync(Namespace, filePath, activeDepSet.Files.Ptr, activeDepSet.Files.Length, activeArchive.Archive.JobHandle);
                ActiveFiles.TryAdd(fileId, activeFile);
#if ENABLE_PROFILER
                RuntimeContentManagerProfiler.RecordLoadFile();
#endif
                return activeFile;
            }
            activeFile.ReferenceCount++;
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loaded file {fileId} - {activeFile.ReferenceCount}");
#endif
            ActiveFiles[fileId] = activeFile;
            return activeFile;
        }

        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        static ActiveArchive LoadArchive(ContentArchiveId archiveId)
        {
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loading archive {archiveId}");
#endif
            if (!ActiveArchives.TryGetValue(archiveId, out var activeArchive))
            {
                if (!Catalog.TryGetArchiveLocation(archiveId, out string archivePath))
                    throw new Exception($"Invalid archive location: {archiveId}");
                activeArchive = new ActiveArchive() { ArchiveId = archiveId, ReferenceCount = 1 };
                if (archiveId.IsValid)
                {
#if ENABLE_CONTENT_DIAGNOSTICS
                    LogFunc?.Invoke($"ArchiveFileInterface.Archive_Mount({archivePath})");
#endif
                    activeArchive.Archive = ArchiveFileInterface.MountAsync(Namespace, archivePath, ArchivePrefix);
                }
                ActiveArchives.TryAdd(archiveId, activeArchive);
#if ENABLE_PROFILER
                RuntimeContentManagerProfiler.RecordLoadArchive();
#endif
                return activeArchive;
            }
            activeArchive.ReferenceCount++;
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loaded archive {archiveId} - {activeArchive.ReferenceCount}");
#endif
            ActiveArchives[archiveId] = activeArchive;
            return activeArchive;
        }

        /// <summary>
        /// Gets the current status of an active scene.
        /// </summary>
        /// <param name="sceneId">The id of the scene.</param>
        /// <returns>The status of the scene loading process.</returns>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        public static SceneLoadingStatus GetSceneLoadingStatus(UntypedWeakReferenceId sceneId)
        {
            if (!ActiveScenes.TryGetValue(sceneId, out var activeScene))
                return SceneLoadingStatus.Failed;
            return activeScene.SceneFile.Status;
        }

        /// <summary>
        /// The scene file.  This is needed to integrate when the scene is loaded with the <seealso cref=" Unity.Loading.ContentSceneParameters.autoIntegrate"/> value set to false.
        /// </summary>
        /// <param name="sceneId">The runtime id of the scene.</param>
        /// <returns>The scene file. If the scene is not loaded, the returned scene will be invalid.</returns>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        public static ContentSceneFile GetSceneFileValue(UntypedWeakReferenceId sceneId)
        {
            if (!ActiveScenes.TryGetValue(sceneId, out var activeScene))
                return default;
            return activeScene.SceneFile;
        }

        /// <summary>
        /// The loaded scene value.
        /// </summary>
        /// <param name="sceneId">The runtime id of the scene.</param>
        /// <returns>The scene. If the scene is not loaded, the returned scene will be invalid.</returns>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        public static Scene GetSceneValue(UntypedWeakReferenceId sceneId)
        {
            if (!ActiveScenes.TryGetValue(sceneId, out var activeScene))
                return default;
            return activeScene.SceneFile.Scene;
        }

        //update the object cache, returns true if the cache was updated
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        static ObjectLoadingStatus ComputeObjectLoadingStatus(UntypedWeakReferenceId objectId)
        {
            if (!ActiveObjects.TryGetValue(objectId, out var activeObject))
                return ObjectLoadingStatus.None;

            if (!ActiveFiles.TryGetValue(activeObject.ContentFileId, out var activeFile))
            {
#if UNITY_EDITOR
                return OverrideLoader.GetObjectLoadStatus(objectId);
#else
                return ObjectLoadingStatus.Error;
#endif
            }
            else
            {
                var fileStatus = activeFile.File.LoadingStatus;
                if (fileStatus == LoadingStatus.Completed)
                    return ObjectLoadingStatus.Completed;
                else if (fileStatus == LoadingStatus.InProgress)
                    return ObjectLoadingStatus.Loading;
                else
                    return ObjectLoadingStatus.Error;
            }
        }

        /// <summary>
        /// Release multiple objects.
        /// </summary>
        /// <param name="pObjectIds">Pointer to the object id array.</param>
        /// <param name="count">The number of objects in the array.</param>
        [BurstCompile]
        [GenerateTestsForBurstCompatibility]
        public unsafe static void ReleaseObjects(UntypedWeakReferenceId* pObjectIds, int count)
        {
            SharedStaticObjectReleaseQueue.Data.Produce(pObjectIds, count);
        }

        /// <summary>
        /// Load multiple objects.
        /// </summary>
        /// <param name="pObjectIds">Pointer to the object id array.</param>
        /// <param name="count">The number of objects in the array.</param>
        [BurstCompile]
        [GenerateTestsForBurstCompatibility]
        public unsafe static void LoadObjectsAsync(UntypedWeakReferenceId *pObjectIds, int count)
        {
            SharedStaticObjectLoadQueue.Data.Produce(pObjectIds, count);
            SharedStaticObjectValueCache.Data.AddEntries(pObjectIds, count);
        }

        /// <summary>
        /// Begins the process of loading an object.  This may trigger the loading of content archives and any dependencies.
        /// </summary>
        /// <param name="objectId">The id of the object.</param>
        /// <returns>True if the process started, false if any errors are encountered.</returns>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        private unsafe static bool LoadObjectImpl(UntypedWeakReferenceId objectId)
        {
#if ENABLE_PROFILER
            RuntimeContentManagerProfiler.RecordLoadObjectRequest();
#endif

#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loading object {objectId}");
#endif
            if (!ActiveObjects.TryGetValue(objectId, out var activeObject))
            {
                SharedStaticObjectValueCache.Data.SetObjectStatus(objectId, ObjectLoadingStatus.Loading, default);
                SharedStaticLoadingObjects.Data.Add(objectId);
                ContentFileId fileId = default;
                long localFileId = default;
                if (!Catalog.TryGetObjectLocation(objectId, out fileId, out localFileId))
                {
#if UNITY_EDITOR
                    if (!OverrideLoader.LoadObject(objectId))
                    {
#if ENABLE_CONTENT_DIAGNOSTICS
                        LogFunc?.Invoke($"Invalid object location: {objectId}");
#endif
                        return false;
                    }
#else

#if ENABLE_CONTENT_DIAGNOSTICS
                    LogFunc?.Invoke($"Invalid object location : {objectId}");
#endif

                    return false;
#endif
                }
                if (fileId.IsValid)
                    LoadFile(fileId);

                activeObject = new ActiveObject() { ObjectReferenceId = objectId, LocalIdentifierInFile = localFileId, ContentFileId = fileId, ReferenceCount = 1 };
                ActiveObjects.TryAdd(objectId, activeObject);
#if ENABLE_PROFILER
                RuntimeContentManagerProfiler.RecordLoadObject();
#endif
                return true;
            }

            activeObject.ReferenceCount++;
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loaded object {objectId} - {activeObject.ReferenceCount}");
#endif
            ActiveObjects[objectId] = activeObject;
            return true;
        }

        /// <summary>
        /// Load a scene.
        /// </summary>
        /// <param name="sceneId">The runtime id of the scene.</param>
        /// <param name="loadParams">Parameters to control how the scene is loaded.</param>
        /// <returns>The scene that was requested to load.  This scene is not loaded at this point.</returns>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        unsafe public static Scene LoadSceneAsync(UntypedWeakReferenceId sceneId, ContentSceneParameters loadParams)
        {
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loading scene {sceneId}");
#endif
            if (!ActiveScenes.TryGetValue(sceneId, out var activeScene))
            {
                ContentFileId fileId = default;
                if (!Catalog.TryGetSceneLocation(sceneId, out fileId, out var sceneName))
                {
#if UNITY_EDITOR
                    if (!OverrideLoader.LoadObject(sceneId))
                    {
#if ENABLE_CONTENT_DIAGNOSTICS
                        LogFunc?.Invoke($"Invalid scene location: {sceneId}");
#endif
                        return default;
                    }
                    activeScene = new ActiveScene { SceneId = sceneId, FileId = fileId, ArchiveId = default, ReferenceCount = 1 };
                    activeScene.ReferenceCount++;
#if ENABLE_CONTENT_DIAGNOSTICS
                    LogFunc?.Invoke($"Loaded scene {sceneId} - {activeScene.ReferenceCount}");
#endif
                    ActiveScenes.TryAdd(sceneId, activeScene);
                    return OverrideLoader.GetScene(sceneId);

#else

#if ENABLE_CONTENT_DIAGNOSTICS
                    LogFunc?.Invoke($"Invalid scene location : {sceneId}");
#endif
                    return default;
#endif
                }

                if (!Catalog.TryGetFileLocation(fileId, out string filePath, out var deps, out var archiveId, out var depIndex))
                    throw new Exception($"Invalid file location: {fileId} for scene {sceneId}");
                activeScene = new ActiveScene { SceneId = sceneId, FileId = fileId, ArchiveId = archiveId, ReferenceCount = 1 };
                LoadActiveDependencySet(depIndex, in deps, out var activeDepSet);

                var activeArchive = archiveId.IsValid ? LoadArchive(archiveId) : default;
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Starting file load for {sceneId}");
#endif
                if (sceneId.IsValid && !string.IsNullOrEmpty(filePath) && archiveId.IsValid)
                    activeScene.SceneFile = ContentLoadInterface.LoadSceneAsync(Namespace, filePath, sceneName, loadParams, activeDepSet.Files.Ptr, activeDepSet.Files.Length, activeArchive.Archive.JobHandle);
                ActiveScenes.TryAdd(sceneId, activeScene);
                return activeScene.SceneFile.Scene;
            }
            activeScene.ReferenceCount++;

#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Loaded scene {sceneId} - {activeScene.ReferenceCount}");
#endif
            ActiveScenes[sceneId] = activeScene;
            return activeScene.SceneFile.Scene;
        }

        /// <summary>
        /// Release a scene.  If the reference count goes to zero, the scene will be unloaded.
        /// </summary>
        /// <param name="sceneId">The scene runtime id.</param>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        public static void ReleaseScene(UntypedWeakReferenceId sceneId)
        {
            if (!ActiveScenes.TryGetValue(sceneId, out var activeScene))
            {
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Releasing scene {sceneId}, not found.");
#endif
                return;
            }
#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Releasing scene {activeScene.SceneId} - {activeScene.ReferenceCount - 1}");
#endif
            if (--activeScene.ReferenceCount <= 0)
            {
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Removing scene {activeScene.SceneId}");
#endif
                if (!Catalog.TryGetFileLocation(activeScene.FileId, out int _, out var deps, out var _, out var depIndex))
                {
#if UNITY_EDITOR
                    OverrideLoader.Unload(sceneId);
                    return;
#else
                    throw new Exception($"Invalid file location: {activeScene.FileId}");
#endif
                }
                activeScene.SceneFile.UnloadAtEndOfFrame();
                ReleaseArchive(activeScene.ArchiveId);
                ReleaseActiveDependencySet(depIndex, in deps);
                ActiveScenes.Remove(sceneId);

            }
            else
            {
                ActiveScenes[sceneId] = activeScene;
            }

        }

        /// <summary>
        /// Release an object.  This will decrement the internal reference count and may not immediately unload the object.
        /// </summary>
        /// <param name="objectId">The object id to release.</param>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        private unsafe static bool ReleaseObjectImpl(UntypedWeakReferenceId objectId)
        {
#if ENABLE_PROFILER
            RuntimeContentManagerProfiler.RecordReleaseObjectRequest();
#endif

            if (!ActiveObjects.TryGetValue(objectId, out var activeObject))
            {
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Releasing object {objectId}, not found.");
#endif
                return false;
            }

#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Releasing object {activeObject.ObjectReferenceId} - {activeObject.ReferenceCount - 1}");
#endif
            if (--activeObject.ReferenceCount <= 0)
            {
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Removing object {activeObject.ObjectReferenceId}");
#endif
                SharedStaticObjectValueCache.Data.RemoveEntry(objectId);
                ActiveObjects.Remove(objectId);
#if ENABLE_PROFILER
                RuntimeContentManagerProfiler.RecordReleaseObject();
#endif
                if (!activeObject.ContentFileId.IsValid)
                {
#if UNITY_EDITOR
                    OverrideLoader.Unload(objectId);
#endif
                }
                else
                {
                    ReleaseFile(activeObject.ContentFileId);
                }
            }
            else
            {
                ActiveObjects[objectId] = activeObject;
            }
            return true;
        }

        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        private static void ReleaseFile(ContentFileId fileId)
        {
            if (!ActiveFiles.TryGetValue(fileId, out var activeFile))
                throw new Exception($"Attempt to release inactive file location: {fileId}");

#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Releasing file {activeFile.ContentFileId} - {activeFile.ReferenceCount - 1}");
#endif
            if (--activeFile.ReferenceCount <= 0)
            {
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Unloading file {activeFile.ContentFileId}");
#endif
                ActiveFiles.Remove(activeFile.ContentFileId);
#if ENABLE_PROFILER
                RuntimeContentManagerProfiler.RecordUnloadFile();
#endif

                if (activeFile.File.IsValid)
                {
                    activeFile.File.UnloadAsync();
                    ReleaseArchive(activeFile.ArchiveId);
                }

                if (!Catalog.TryGetFileLocation(fileId, out int _, out var deps, out var _, out var depIndex))
                    throw new Exception($"Invalid file location: {fileId}");
                ReleaseActiveDependencySet(depIndex, in deps);
            }
            else
            {
                ActiveFiles[fileId] = activeFile;
            }
        }

        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        private static void ReleaseArchive(ContentArchiveId archiveId)
        {
            if (!ActiveArchives.TryGetValue(archiveId, out var activeArchive))
                throw new Exception($"Invalid archive id: {archiveId}");

#if ENABLE_CONTENT_DIAGNOSTICS
            LogFunc?.Invoke($"Releasing archive {activeArchive.ArchiveId} - {activeArchive.ReferenceCount - 1}");
#endif
            if (--activeArchive.ReferenceCount <= 0)
            {
                if (!Catalog.TryGetArchiveLocation(archiveId, out int _))
                    throw new Exception($"Invalid archive location: {archiveId}");

#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"Unmounting archive {activeArchive.ArchiveId}");
#endif
                ActiveArchives.Remove(activeArchive.ArchiveId);
                if (activeArchive.ArchiveId.IsValid)
                    activeArchive.Archive.Unmount();
#if ENABLE_PROFILER
                RuntimeContentManagerProfiler.RecordUnloadArchive();
#endif
            }
            else
            {
                ActiveArchives[archiveId] = activeArchive;
            }
        }

        /// <summary>
        /// Blocks on the main thread until the load operation completes. This function can be slow and so should be used carefully to avoid frame rate stuttering.
        /// </summary>
        /// <param name="objectId">The id of the object to wait for.</param>
        /// <param name="timeoutMs">The maximum time in milliseconds this function will wait before returning. Pass 0 to block indefinitely until completion.</param>
        /// <returns> Returns false if the timeout was reached before ContentFile completed loading or if the object is not loading.</returns>
        /// <exception cref="Exception">An exception will be thrown if the internal file data is invalid.</exception>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        public static bool WaitForObjectCompletion(UntypedWeakReferenceId objectId, int timeoutMs = 0)
        {
            ProcessQueuedCommands();
            if (!ActiveObjects.TryGetValue(objectId, out var activeObject))
                return false;

            if (!ActiveFiles.TryGetValue(activeObject.ContentFileId, out var activeFile))
            {
#if UNITY_EDITOR
                if (!OverrideLoader.WaitForCompletion(objectId))
                    return false;
                UpdateLoadingObjectStatus();
                return true;
#else
                throw new Exception($"Unable to find file location for object id {activeObject.ContentFileId}");
#endif
            }

            if (!activeFile.File.IsValid || activeFile.File.LoadingStatus == LoadingStatus.Failed)
                return false;

            if (activeFile.File.LoadingStatus == LoadingStatus.Completed)
                return true;

            if (!activeFile.File.WaitForCompletion(timeoutMs))
                return false;
            UpdateLoadingObjectStatus();
            return true;
        }

        /// <summary>
        /// Get the cached object status value.  This method is thread safe and can be burst compiled.  The cached value is only update once per frame.
        /// </summary>
        /// <param name="objectId">The object id.</param>
        /// <returns>The loading status of the object.</returns>
        [BurstCompile]
        [GenerateTestsForBurstCompatibility]
        public unsafe static ObjectLoadingStatus GetObjectLoadingStatus(in UntypedWeakReferenceId objectId)
        {
            return SharedStaticObjectValueCache.Data.GetLoadingStatus(objectId);
        }

        /// <summary>
        /// For an object that is loaded, this will return the loaded value.
        /// </summary>
        /// <typeparam name="TObject">The type of object to access.</typeparam>
        /// <param name="objectId">The object id.</param>
        /// <returns>The reference to the object.</returns>
        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        public static TObject GetObjectValue<TObject>(UntypedWeakReferenceId objectId) where TObject : UnityEngine.Object
        {
            GCHandle handle = default;
            if (!GetObjectHandle(objectId, ref handle))
                return default(TObject);
            return handle.Target as TObject;
        }

        /// <summary>
        /// For an object that is loaded, this will return the loaded value handle.  This method can be call from bursted code and background threads.
        /// </summary>
        /// <typeparam name="TObject">The type of object to access.</typeparam>
        /// <param name="objectId">The object id.</param>
        /// <returns>The reference to the object.</returns>
        [BurstCompile]
        unsafe static bool GetObjectHandle(in UntypedWeakReferenceId objectId, ref GCHandle objectHandle)
        {
            return SharedStaticObjectValueCache.Data.GetObjectHandle(objectId, ref objectHandle);
        }

        [ExcludeFromBurstCompatTesting("References managed engine API and static data")]
        static GCHandle GetObjectHandleImpl(UntypedWeakReferenceId objectId)
        {
            if (!ActiveObjects.TryGetValue(objectId, out var activeObject))
                return default;

            if (!ActiveFiles.TryGetValue(activeObject.ContentFileId, out var activeFile))
            {
#if UNITY_EDITOR
                var result = OverrideLoader.GetObject(objectId);
                return result == null ? default : GCHandle.Alloc(result);
#endif
                throw new Exception($"Unable to find file location for object id {activeObject.ContentFileId}");
            }

            if (!activeFile.File.IsValid || activeFile.File.LoadingStatus == LoadingStatus.Failed)
            {
#if ENABLE_CONTENT_DIAGNOSTICS
                LogFunc?.Invoke($"GetObject failed to load file {activeObject.ContentFileId}");
#endif
                return default;
            }

            var result2 = activeFile.File.GetObject((ulong)activeObject.LocalIdentifierInFile);
            return result2 == null ? default : GCHandle.Alloc(result2);
        }


#if UNITY_EDITOR
        internal interface IAlternativeLoader : IDisposable
        {
            bool IsCreated { get; }
            bool LoadObject(UntypedWeakReferenceId referenceId);
            ObjectLoadingStatus GetObjectLoadStatus(UntypedWeakReferenceId referenceId);
            object GetObject(UntypedWeakReferenceId objectReferenceId);
            Scene GetScene(UntypedWeakReferenceId sceneReferenceId);
            void Unload(UntypedWeakReferenceId referenceId);
            bool WaitForCompletion(UntypedWeakReferenceId referenceId);
        }

        internal static IAlternativeLoader OverrideLoader;
#endif
    }
}
#endif
