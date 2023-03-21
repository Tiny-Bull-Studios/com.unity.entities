using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;
using Unity.Editor.Bridge;

namespace Unity.Entities.Editor
{
    public static class HierarchySearchProvider
    {
        public const string type = "nodehierarchy";
        static Hierarchy s_Hierarchy;
        static QueryEngine<HierarchyNode.Immutable> m_EntityQueryEngine;
        static QueryEngine<HierarchyNode.Immutable> s_EntityQueryEngine
        {
            get
            {
                if (m_EntityQueryEngine == null)
                {
                    SetupQueries();
                }
                return m_EntityQueryEngine;
            }
        }
        static HashSet<string> s_EntityFilters;
        static HashSet<string> s_HierarchyFilters;

        internal class HierarchyQueryDescriptor
        {
            public HierarchyQueryDescriptor(string query)
            {
                originalQueryStr = query;
                processedQueryStr = PreprocessQuery(query);
                unusedFilters = "";
                searchValueTokenStr = "";
                searchValueTokens = new string[0];
                dataMode = DataMode.Authoring;
            }

            public string parsingErrors;
            public string originalQueryStr;
            public string processedQueryStr;
            public ParsedQuery<HierarchyNode.Immutable> query;
            public string world;
            public DataMode dataMode;
            public EntityQueryDesc entityQuery;
            public HierarchyQueryBuilder.Result entityQueryResult;
            public int entityIndex = -1;
            public NodeKind kind = NodeKind.None;
            public string[] searchValueTokens;
            public string searchValueTokenStr;
            public string unusedFilters;
        }

        internal static void SetupQueries()
        {
            m_EntityQueryEngine = new();
            s_EntityFilters = new();
            s_EntityFilters = new();

            s_EntityQueryEngine.AddFilter("w", node => "dummyworld", new[] { "=" });
            s_EntityQueryEngine.AddFilter("dm", node => "dummymode", new[] { "=" });

            // Tag all hierarchy filters.
            SearchBridge.SetFilter(s_EntityQueryEngine, "ei", node => node.GetHandle().Index, new[] { "=" })
                .AddOrUpdateProposition(category: null, label: "Entity Index", replacement: "ei=1", help: "Search entities by index");
            s_EntityQueryEngine.AddFilter("c", node => "dummytype", new[] { "=" });
            s_EntityQueryEngine.AddFilter("none", node => "dummytype", new[] { "=" });
            s_EntityQueryEngine.AddFilter("any", node => "dummytype", new[] { "=" });
            s_EntityQueryEngine.AddFilter("all", node => "dummytype", new[] { "=" });
            s_EntityQueryEngine.AddFilter("k", node => node.GetHandle().Kind, new[] { "=" });

            s_EntityFilters = new HashSet<string>(new[] { "c", "none", "all", "any" });
            s_HierarchyFilters = new HashSet<string>(new[] { "ei", "k", "w", "dm" });

            // Add Node filter to be filtered while yielding:
            SearchBridge.SetFilter(s_EntityQueryEngine, "depth", node => node.GetDepth())
                .AddOrUpdateProposition(category: null, label: "Depth", replacement: "depth=1", help: "Search entities by Depth");
            SearchBridge.SetFilter(s_EntityQueryEngine, "child", node => node.GetChildCount()).
                AddOrUpdateProposition(category: null, label: "Child count", replacement: "child>0", help: "Search entities by number of child");
            SearchBridge.SetFilter(s_EntityQueryEngine, "disabled", node => s_Hierarchy.IsDisabled(node.GetHandle())).
                AddOrUpdateProposition(category: null, label: "Disabled", replacement: "disabled=true", help: "Search disabled entities");
            SearchBridge.SetFilter(s_EntityQueryEngine, "prefabtype", node => s_Hierarchy.GetPrefabType(node.GetHandle()));

            s_EntityQueryEngine.validateFilters = true;
            s_EntityQueryEngine.skipUnknownFilters = false;
            s_EntityQueryEngine.SetSearchDataCallback(node => new string[0]);
        }

        static void OnEnable()
        {
            s_Hierarchy = new Hierarchy(Allocator.Persistent, DataMode.Authoring);
            s_Hierarchy.Configuration.UpdateMode = Hierarchy.UpdateModeType.Synchronous;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnDisable()
        {
            s_Hierarchy?.Dispose();
            s_Hierarchy = null;
            m_EntityQueryEngine = null;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange stateChange)
        {
            if (stateChange == PlayModeStateChange.EnteredPlayMode || stateChange == PlayModeStateChange.EnteredEditMode)
            {
                SearchBridge.RefreshWindowsWithProvider(type);
            }
        }

        // TODO Search : listen to scene change (See hierarchywindow)
        static readonly Regex k_NoneComponent = new Regex(
            @$"-(?<token>[{Constants.ComponentSearch.TokenCaseInsensitive}]{Constants.ComponentSearch.Op})(?<componentType>(\S*))",
            RegexOptions.Compiled | RegexOptions.Singleline);

        static readonly Regex k_AllComponent = new Regex(
            @$"(?<token>[{Constants.ComponentSearch.TokenCaseInsensitive}]{Constants.ComponentSearch.Op})(?<componentType>(\S*))",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.ExplicitCapture);


        internal static string PreprocessQuery(string query)
        {
            var noneQuery = k_NoneComponent.Replace(query, m => {
                return $"none={m.Groups["componentType"]}";
            });
            var allQuery = k_AllComponent.Replace(noneQuery, m =>
            {
                return $"all={m.Groups["componentType"]}";
            });
            return allQuery;
        }

        internal static HierarchyFilter CreateHierarchyFilter(HierarchySearch search, HierarchyQueryDescriptor desc, Allocator allocator)
        {
            var filter = new HierarchyFilter(search, desc.entityQueryResult, desc.searchValueTokens, desc.entityIndex, desc.kind, allocator, parseTokensForFilter: false);
            if (!desc.query.valid && desc.query.errors.Count > 0)
            {
                filter.ErrorCategory = "Invalid Query";
                filter.ErrorMsg = string.Join(",", desc.query.errors.Select(e => e.reason));
            }
            return filter;
        }
        
        internal static HierarchyQueryDescriptor CreateHierarchyQueryDescriptor(string query)
        {
            var desc = new HierarchyQueryDescriptor(query);

            desc.query = s_EntityQueryEngine.ParseQuery(desc.processedQueryStr);
            if (desc.query.errors.Any())
                return desc;

            var toggles = new List<IQueryNode>();
            var filters = new List<IFilterNode>();
            var searches = new List<ISearchNode>();
            SearchUtils.GetQueryParts(desc.query.queryGraph.root, filters, toggles, searches);
            var worldFilter = filters.Where(f => f.filterId == "w").FirstOrDefault();
            if (worldFilter != null)
            {
                desc.world = worldFilter.filterValue;
            }

            var dataModeFilter = filters.Where(f => f.filterId == "dm").FirstOrDefault();
            if (dataModeFilter != null && Enum.TryParse<DataMode>(dataModeFilter.filterValue, true, out var dm))
            {
                desc.dataMode = dm;
            }

            desc.searchValueTokens = searches.Select(s => s.searchValue).ToArray();
            desc.searchValueTokenStr = string.Join(" ", desc.searchValueTokens);

            var entityFilters = filters.Where(f => s_EntityFilters.Contains(f.filterId)).ToArray();
            if (entityFilters.Any())
            {
                var entityQueryOptions = EntityQueryOptions.Default;
                foreach (var toggle in toggles)
                {
                    var toggleStr = toggle.token.text;
                    if (toggleStr.StartsWith("+"))
                        toggleStr = toggleStr.Substring(1);
                    if (Enum.TryParse<EntityQueryOptions>(toggleStr, true, out var options))
                    {
                        entityQueryOptions |= options;
                    }
                }

                string unknownComponent = null;
                var all = GetComponentTypes(entityFilters, "all", ref unknownComponent);
                var none = GetComponentTypes(entityFilters, "none", ref unknownComponent);
                var any = GetComponentTypes(entityFilters, "any", ref unknownComponent);
                if (all == null || none == null || any == null)
                {
                    desc.entityQueryResult = HierarchyQueryBuilder.Result.Invalid(unknownComponent);
                }
                else
                {
                    desc.entityQuery = new EntityQueryDesc()
                    {
                        All = all,
                        None = none,
                        Any = any,
                        Options = entityQueryOptions
                    };

                    desc.entityQueryResult = HierarchyQueryBuilder.Result.Valid(desc.entityQuery, desc.searchValueTokenStr);
                }
            }
            else
            {
                desc.entityQueryResult = HierarchyQueryBuilder.Result.Valid(null, desc.searchValueTokenStr);
            }

            var entityIndexFilter = filters.Where(f => f.filterId == "ei").FirstOrDefault();
            if (entityIndexFilter != null)
                desc.entityIndex = Convert.ToInt32(entityIndexFilter.filterValue);

            var kindFilter = filters.Where(f => f.filterId == "k").FirstOrDefault();
            if (kindFilter != null)
            {
                desc.kind = SearchUtils.ParseEnum<NodeKind>(kindFilter.filterValue);
            }

            var unusedFilters = filters.Where(f => !s_EntityFilters.Contains(f.filterId) && !s_HierarchyFilters.Contains(f.filterId)).Select(f => f.token.text);
            desc.unusedFilters = string.Join(" ", unusedFilters);

            return desc;
        }

        static ComponentType[] GetComponentTypes(IEnumerable<IFilterNode> filters, string filterName, ref string unknownComponent)
        {
            return GetComponentTypes(filters.Where(f => f.filterId == filterName).Select(f => f.filterValue), ref unknownComponent);
        }

        internal static ComponentType[] GetComponentTypes(IEnumerable<string> strs, ref string unknownComponent)
        {
            var entityTypeIndex = TypeManager.GetTypeIndex<Entity>();
            var types = new List<Type>();
            foreach(var name in strs)
            {
                var typesForName = ComponentTypeCache.GetExactMatchingTypes(name);
                if (!typesForName.Any())
                {
                    unknownComponent = name;
                    return null;
                }
                types.AddRange(typesForName);
            }

            return types.Select(t => (ComponentType)t ).Where(t => t.TypeIndex != entityTypeIndex).ToArray();
        }

        static IEnumerable<SearchItem> FetchItems(SearchContext context, SearchProvider provider)
        {
            var queryStr = context.searchQuery;
            if (string.IsNullOrEmpty(queryStr))
                yield break;

            var queryDesc = CreateHierarchyQueryDescriptor(queryStr);
            if (!queryDesc.query.valid)
            {
                foreach (var e in queryDesc.query.errors)
                    Debug.LogError(e.reason);
                yield break;
            }

            var world = SearchUtils.FindWorld(queryDesc.world);
            if (world == null)
                yield break;

            s_Hierarchy.SetWorld(world);

            if (s_Hierarchy.DataMode != queryDesc.dataMode)
            {
                s_Hierarchy.Dispose();
                s_Hierarchy = new Hierarchy(Allocator.Persistent, s_Hierarchy.DataMode);
                s_Hierarchy.Configuration.UpdateMode = Hierarchy.UpdateModeType.Synchronous;
            }

            var filter = CreateHierarchyFilter(s_Hierarchy.HierarchySearch, queryDesc, s_Hierarchy.Allocator);
            if (!filter.IsValid)
            {
                yield break;
            }

            s_Hierarchy.SetFilter(filter);

            // Wait for search to happen:
            var changeVersion = s_Hierarchy.GetNodes().ChangeVersion;
            s_Hierarchy.Update(true);
            while (changeVersion == s_Hierarchy.GetNodes().ChangeVersion)
                yield return null;

            // Yield resulting nodes.
            ParsedQuery<HierarchyNode.Immutable> nodeQuery = null;
            if (!string.IsNullOrEmpty(queryDesc.unusedFilters))
            {
                // Debug.Log($"Node Query: {queryDesc.unusedFilters}");
                nodeQuery = s_EntityQueryEngine.ParseQuery(queryDesc.unusedFilters);
            }

            var nodes = s_Hierarchy.GetNodes();
            for(var i = 0; i < nodes.Count; ++i)
            {
                var immutableNode = nodes[i];
                if (nodeQuery != null && !nodeQuery.Test(immutableNode))
                    continue;

                var handle = immutableNode.GetHandle();
                var name = s_Hierarchy.GetName(handle);
                var kind = handle.Kind;
                Texture2D icon = null;
                switch(kind)
                {
                    case NodeKind.Scene:
                    case NodeKind.SubScene:
                        icon = SearchUtils.sceneAssetIcon;
                        break;
                    case NodeKind.Entity:
                        {
                            icon = SearchUtils.entityIcon;
                            var prefabType = s_Hierarchy.GetPrefabType(handle);
                            if (prefabType == Hierarchy.HierarchyPrefabType.PrefabRoot)
                                icon = SearchUtils.entityPrefabIcon;
                            break;
                        }
                    case NodeKind.GameObject:
                        {
                            icon = SearchUtils.gameObjectIcon;
                            var prefabType = s_Hierarchy.GetPrefabType(handle);
                            if (prefabType == Hierarchy.HierarchyPrefabType.PrefabRoot)
                                icon = SearchUtils.prefabIcon;
                            break;
                        }
                }

                yield return provider.CreateItem(context, handle.GetHashCode().ToString(), i, name, kind.ToString(), icon, immutableNode);
            }
        }

        static IEnumerable<SearchProposition> FetchPropositions(SearchContext context, SearchPropositionOptions options)
        {
            if (!options.flags.HasAny(SearchPropositionFlags.QueryBuilder))
                yield break;

            foreach (var p in SearchBridge.GetPropositions(s_EntityQueryEngine))
                yield return p;

            foreach (var p in SearchBridge.GetPropositionsFromListBlockType(typeof(QueryWorldBlock)))
                yield return p;

            foreach (var p in SearchBridge.GetPropositionsFromListBlockType(typeof(QueryAllComponentTypeBlock)))
                yield return p;

            foreach (var p in SearchBridge.GetPropositionsFromListBlockType(typeof(QueryNotAllComponentTypeBlock)))
                yield return p;

            foreach (var p in SearchBridge.GetPropositionsFromListBlockType(typeof(QueryAnyComponentTypeBlock)))
                yield return p;

            foreach (var p in SearchBridge.GetPropositionsFromListBlockType(typeof(QueryNodeKindBlock)))
                yield return p;

            foreach (var p in SearchBridge.GetPropositionsFromListBlockType(typeof(QueryPrefabTypeBlock)))
                yield return p;

            foreach (var p in SearchBridge.GetEnumToggle("Entity Query Options", "Refine entity query",
                EntityQueryOptions.FilterWriteGroup,
                EntityQueryOptions.IgnoreComponentEnabledState,
                EntityQueryOptions.IncludeDisabledEntities,
                EntityQueryOptions.IncludePrefab, EntityQueryOptions.IncludeSystems))
                yield return p;
        }


        private static SearchTable GetDefaultTableConfig(SearchContext context)
        {
            return new SearchTable(type, new[] { new SearchColumn("Name", "label") }.Concat(FetchColumns(context, null)));
        }

        private static UnityEngine.Object PingItem(SearchItem item)
        {
            var obj = item.ToObject<GameObject>();
            if (obj == null)
                return null;
            EditorGUIUtility.PingObject(obj);
            return obj;
        }

        private static void FrameObjects(UnityEngine.Object[] objects)
        {
            Selection.instanceIDs = objects.Select(o => o.GetHashCode()).ToArray();
            if (SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();
        }

        [SearchActionsProvider]
        internal static IEnumerable<SearchAction> ActionHandlers()
        {
            return new SearchAction[]
            {
                new SearchAction(type, "select", null, "Select object(s) in scene...")
                {
                    execute = (items) =>
                    {
                        SelectItem(items.First());
                    },
                    closeWindowAfterExecution = false
                }
            };
        }

        [SearchItemProvider]
        internal static SearchProvider CreateProvider()
        {
            var p = new SearchProvider(type, "Node Hierarchy")
            {
                type = type,
                filterId = "nh:",
                onEnable = OnEnable,
                onDisable = OnDisable,
                fetchColumns = FetchColumns,
                isExplicitProvider = true,
                active = true,
                priority = 2500,
                fetchItems = (context, items, provider) => FetchItems(context, provider),
                fetchPropositions = (context, options) => FetchPropositions(context, options),
                fetchThumbnail = SearchUtils.DefaultFetchThumbnail,
                fetchLabel = SearchUtils.DefaultFetchLabel,
                fetchDescription = SearchUtils.DefaultFetchDescription,
                toObject = ToObject,
                trackSelection = SelectItem,
                showDetails = true,
                showDetailsOptions = ShowDetailsOptions.Default | ShowDetailsOptions.Inspector,
            };
            SearchBridge.SetTableConfig(p, GetDefaultTableConfig);
            return p;
        }

        static string FetchDescription(SearchItem item, SearchContext context)
        {
            var node = (HierarchyNode.Immutable)item.data;
            var kind = node.GetHandle().Kind.ToString();
            if (item.options.HasFlag(SearchItemOptions.Compacted))
            {
                return $"{item.label} - {kind}";
            }
            return kind;
        }

        static UnityEngine.Object ToObject(SearchItem item, Type type)
        {
            var node = (HierarchyNode.Immutable)item.data;
            switch(node.GetHandle().Kind)
            {
                case NodeKind.GameObject:
                    return node.GetHandle().ToGameObject();
                default:
                    return null;
            }
        }

        static void SelectItem(SearchItem item, SearchContext ctx = null)
        {
            var node = (HierarchyNode.Immutable)item.data;
            HierarchyWindow.SelectHierarchyNode(s_Hierarchy, node.GetHandle(), DataMode.Mixed);
        }

        [SearchColumnProvider(nameof(HierarchyNode))]
        public static void HierarchyModeColumnProvider(SearchColumn column)
        {
            column.getter = args =>
            {
                if (args.item.data is HierarchyNode.Immutable node)
                {
                    switch (column.selector)
                    {
                        case "child_count": return node.GetChildCount();
                        case "depth": return node.GetDepth();
                        case "kind": return node.GetHandle().Kind;
                        case "disabled": return s_Hierarchy.IsDisabled(node.GetHandle());
                        case "index": return node.GetHandle().Index;
                        case "instanceId": return s_Hierarchy.GetInstanceId(node.GetHandle());
                        case "prefab": return s_Hierarchy.GetPrefabType(node.GetHandle());
                    }
                }

                return null;
            };
        }

        static IEnumerable<SearchColumn> FetchColumns(SearchContext context, IEnumerable<SearchItem> items)
        {
            yield return new SearchColumn("Entity/Index", "index", nameof(HierarchyNode));
            if (context == null)
            {
                yield break;
            }
            yield return new SearchColumn("Entity/InstanceID", "instanceId", nameof(HierarchyNode));
            yield return new SearchColumn("Entity/Kind", "kind", nameof(HierarchyNode));
            yield return new SearchColumn("Entity/Prefab", "prefab", nameof(HierarchyNode));
            yield return new SearchColumn("Entity/Index", "index", nameof(HierarchyNode));
            yield return new SearchColumn("Entity/InstanceID", "instanceId", nameof(HierarchyNode));
            yield return new SearchColumn("Entity/Disabled", "disabled", nameof(HierarchyNode));
            yield return new SearchColumn("Entity/Child Count", "child_count", nameof(HierarchyNode));
            yield return new SearchColumn("Entity/Depth", "depth", nameof(HierarchyNode));
        }

        [MenuItem("Window/Search/Node Hierarchy", priority = 1371)]
        static void OpenProviderMenu()
        {
            OpenProvider();
        }

        [UnityEditor.ShortcutManagement.Shortcut("Help/Search/Node Hierarchy")]
        static void OpenShortcut()
        {
            OpenProvider();
        }

        public static void OpenProvider(string query = null)
        {
            SearchBridge.OpenContextualTable(type, query ?? "",
                GetDefaultTableConfig(null),
                viewState => {
                    viewState.flags |= UnityEngine.Search.SearchViewFlags.DisableInspectorPreview;
                });
        }
    }
}