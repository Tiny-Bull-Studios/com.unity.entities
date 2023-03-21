using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Unity.Entities.SourceGen.Common;

namespace Unity.Entities.SourceGen.SystemGenerator.Common
{
    public struct ParameterTypeInJobEntityExecuteMethod
    {
        public bool IsReadOnly;
        public ITypeSymbol TypeSymbol;
        public bool IsAspect;

        public override string ToString()
        {
            if (!IsAspect)
                return IsReadOnly
                    ? $"Unity.Entities.ComponentType.ReadOnly<{TypeSymbol.ToFullName()}>()"
                    : $"Unity.Entities.ComponentType.ReadWrite<{TypeSymbol.ToFullName()}>()";
            return $"default({TypeSymbol.ToFullName()}).AddComponentRequirementsTo(ref __ComponentTypesRequiredForExecuteMethodToRun, {(IsReadOnly ? "true" : "false")});";
        }
    }

    public partial class JobEntityDescription : ISourceGeneratorDiagnosable
    {
        readonly List<ParameterTypeInJobEntityExecuteMethod> m_ComponentTypesInExecuteMethod;
        readonly List<ParameterTypeInJobEntityExecuteMethod> m_AspectTypesInExecuteMethod;
        readonly INamedTypeSymbol m_JobEntityTypeSymbol;
        readonly bool m_PerformSafetyChecks;

        public List<Diagnostic> Diagnostics { get; }
        public string FullTypeName => m_JobEntityTypeSymbol.ToFullName();
        string TypeName => m_JobEntityTypeSymbol.Name;
        bool m_RequiresEntityManager;
        bool HasManagedComponents => m_RequiresEntityManager;
        bool m_HasEntityIndexInQuery;

        public bool Invalid { get; private set; }
        public readonly JobEntityParam[] UserExecuteMethodParams;

        HandlesDescription m_HandlesDescription;

        static string GetUserExecuteMethodSignature(IMethodSymbol userExecuteMethod) => $"{userExecuteMethod.Name}({userExecuteMethod.Parameters.Select(p => $"{p.Type.Name} {p.Name}").SeparateByComma()})";

        public JobEntityDescription(StructDeclarationSyntax candidate, SemanticModel semanticModel, bool performSafetyChecks)
        {
            Invalid = false;
            Diagnostics = new List<Diagnostic>();

            m_JobEntityTypeSymbol = semanticModel.GetDeclaredSymbol(candidate);
            m_HandlesDescription = HandlesDescription.Create(candidate);
            m_PerformSafetyChecks = performSafetyChecks;

            // Find valid user made execute method - can likely be shortened if we remove the SGJE0008 error :3
            var userExecuteMethods = new List<IMethodSymbol>();
            foreach (var member in m_JobEntityTypeSymbol.GetMembers())
                if (member is IMethodSymbol method && member.Name == "Execute")
                    userExecuteMethods.Add(method);

            if (userExecuteMethods.Count != 1)
            {
                JobEntityGeneratorErrors.SGJE0008(this, m_JobEntityTypeSymbol.Locations.First(), FullTypeName, userExecuteMethods);
                Invalid = true;
                return;
            }
            var userExecuteMethod = userExecuteMethods[0];

            m_ComponentTypesInExecuteMethod = new List<ParameterTypeInJobEntityExecuteMethod>();
            m_AspectTypesInExecuteMethod = new List<ParameterTypeInJobEntityExecuteMethod>();

            // Generate JobEntityParams
            var queryInfo = QueryInfo.Default();
            UserExecuteMethodParams = new JobEntityParam[userExecuteMethod.Parameters.Length];

            for (var parameterIndex = 0; parameterIndex < userExecuteMethod.Parameters.Length; parameterIndex++)
                UserExecuteMethodParams[parameterIndex] = CreateJobEntityParam(userExecuteMethod.Parameters[parameterIndex], performSafetyChecks, queryInfo.QueryAllTypes, queryInfo.QueryChangeFilterTypes);

            // Generate Query
            queryInfo.FillFromAttributes(candidate.AttributeLists, semanticModel);
            queryInfo.CreateDefaultQuery(ref m_HandlesDescription);

            // Do remaining checks to see if this description is valid
            OutputErrors(userExecuteMethod);
        }

        JobEntityParam CreateJobEntityParam(IParameterSymbol parameterSymbol, bool performSafetyChecks,
            List<Query> queryAllTypes, List<Query> queryChangeFilterTypes)
        {
            var typeSymbol = parameterSymbol.Type;

            var hasChangeFilter = false;
            foreach (var attribute in parameterSymbol.GetAttributes())
            {
                switch (attribute.AttributeClass.ToFullName())
                {
                    case "global::Unity.Entities.ChunkIndexInQuery":
                        return new JobEntityParam_ChunkIndexInQuery(parameterSymbol);
                    case "global::Unity.Entities.EntityIndexInChunk":
                        return new JobEntityParam_EntityIndexInChunk(parameterSymbol);
                    case "global::Unity.Entities.EntityIndexInQuery":
                        m_HasEntityIndexInQuery = true;
                        return new JobEntityParam_EntityIndexInQuery(parameterSymbol);
                    case "global::Unity.Entities.WithChangeFilterAttribute":
                        hasChangeFilter = true;
                        break;
                }
            }

            switch (typeSymbol)
            {
                case INamedTypeSymbol namedTypeSymbol:
                {
                    switch (namedTypeSymbol.Arity)
                    {
                        case 1:
                        {
                            var typeArgSymbol = namedTypeSymbol.TypeArguments.Single();
                            var fullName = namedTypeSymbol.ConstructedFrom.ToFullName();

                            switch (fullName)
                            {
                                // Dynamic Buffer
                                case "global::Unity.Entities.DynamicBuffer<T>":
                                {
                                    var typeHandleName = m_HandlesDescription.GetOrCreateTypeHandleField(typeArgSymbol, parameterSymbol.IsReadOnly());
                                    var jobEntityParameter = new JobEntityParam_DynamicBuffer(parameterSymbol, typeArgSymbol, typeHandleName);
                                    queryAllTypes.Add(new Query
                                    {
                                        IsReadOnly = jobEntityParameter.IsReadOnly,
                                        Type = QueryType.All,
                                        TypeSymbol = jobEntityParameter.TypeSymbol
                                    });
                                    m_ComponentTypesInExecuteMethod.Add(new ParameterTypeInJobEntityExecuteMethod
                                    {
                                        IsReadOnly = jobEntityParameter.IsReadOnly,
                                        TypeSymbol = jobEntityParameter.TypeSymbol,
                                    });
                                    return jobEntityParameter;
                                }

                                // RefX / EnabledRefX
                                case "global::Unity.Entities.RefRW<T>":
                                case "global::Unity.Entities.RefRO<T>":
                                case "global::Unity.Entities.EnabledRefRW<T>":
                                case "global::Unity.Entities.EnabledRefRO<T>":
                                {
                                    if (typeArgSymbol.InheritsFromInterface("Unity.Entities.IComponentData"))
                                    {
                                        var isReadOnly = fullName == "global::Unity.Entities.RefRO<T>" ||
                                            fullName == "global::Unity.Entities.EnabledRefRO<T>";
                                        var typeHandleName = m_HandlesDescription.GetOrCreateTypeHandleField(typeArgSymbol, isReadOnly);
                                        var (success, jobEntityParameter) =
                                            JobEntityParam.TryParseComponentTypeSymbol(
                                                typeArgSymbol,
                                                parameterSymbol,
                                                isReadOnly,
                                                performSafetyChecks,
                                                diagnosable: this,
                                                typeHandleName,
                                                constructedFrom: fullName);

                                        if (success)
                                        {
                                            queryAllTypes.Add(new Query
                                            {
                                                IsReadOnly = jobEntityParameter.IsReadOnly,
                                                Type = QueryType.All,
                                                TypeSymbol = jobEntityParameter.TypeSymbol
                                            });
                                            m_ComponentTypesInExecuteMethod.Add(new ParameterTypeInJobEntityExecuteMethod
                                            {
                                                IsReadOnly = jobEntityParameter.IsReadOnly,
                                                TypeSymbol = jobEntityParameter.TypeSymbol,
                                            });
                                            if (hasChangeFilter)
                                            {
                                                queryChangeFilterTypes.Add(new Query
                                                {
                                                    IsReadOnly = jobEntityParameter.IsReadOnly,
                                                    Type = QueryType.ChangeFilter,
                                                    TypeSymbol = jobEntityParameter.TypeSymbol
                                                });
                                            }
                                            return jobEntityParameter;
                                        }

                                        Invalid = true;
                                        return null;
                                    }

                                    Invalid = true;
                                    JobEntityGeneratorErrors.SGJE0019(this, parameterSymbol.Locations.Single(), typeArgSymbol.ToFullName());
                                    return null;
                                }
                                default:
                                    Invalid = true;
                                    return null;
                            }
                        }
                        case 0:
                        {
                            // Shared Components
                            if (typeSymbol.InheritsFromInterface("Unity.Entities.ISharedComponentData"))
                            {
                                var typeHandleName = m_HandlesDescription.GetOrCreateTypeHandleField(typeSymbol, parameterSymbol.IsReadOnly());
                                m_RequiresEntityManager |= !typeSymbol.IsUnmanagedType;
                                var jobEntityParameter = new JobEntityParam_SharedComponent(parameterSymbol, typeHandleName);
                                queryAllTypes.Add(new Query
                                {
                                    IsReadOnly = jobEntityParameter.IsReadOnly,
                                    Type = QueryType.All,
                                    TypeSymbol = jobEntityParameter.TypeSymbol
                                });
                                m_ComponentTypesInExecuteMethod.Add(new ParameterTypeInJobEntityExecuteMethod
                                {
                                    IsReadOnly = jobEntityParameter.IsReadOnly,
                                    TypeSymbol = jobEntityParameter.TypeSymbol,
                                    IsAspect = false
                                });
                                if (hasChangeFilter)
                                {
                                    queryChangeFilterTypes.Add(new Query
                                    {
                                        IsReadOnly = jobEntityParameter.IsReadOnly,
                                        Type = QueryType.ChangeFilter,
                                        TypeSymbol = jobEntityParameter.TypeSymbol
                                    });
                                }
                                return jobEntityParameter;
                            }

                            // Entity
                            if (typeSymbol.Is("Unity.Entities.Entity"))
                            {
                                var typeHandleName = m_HandlesDescription.GetOrCreateEntityTypeHandleField();
                                return new JobEntityParam_Entity(parameterSymbol, typeHandleName);
                            }
                            if (typeSymbol.IsValueType)
                            {
                                // ComponentData
                                if (typeSymbol.InheritsFromInterface("Unity.Entities.IComponentData"))
                                {
                                    var isReadOnly = parameterSymbol.IsReadOnly();
                                    var typeHandleName = m_HandlesDescription.GetOrCreateTypeHandleField(typeSymbol, isReadOnly);
                                    var (success, jobEntityParameter) =
                                        JobEntityParam.TryParseComponentTypeSymbol(
                                            typeSymbol,
                                            parameterSymbol,
                                            isReadOnly,
                                            performSafetyChecks,
                                            diagnosable: this, typeHandleName);

                                    if (success)
                                    {
                                        queryAllTypes.Add(new Query
                                        {
                                            IsReadOnly = jobEntityParameter.IsReadOnly,
                                            Type = QueryType.All,
                                            TypeSymbol = jobEntityParameter.TypeSymbol
                                        });
                                        m_ComponentTypesInExecuteMethod.Add(new ParameterTypeInJobEntityExecuteMethod
                                        {
                                            IsReadOnly = jobEntityParameter.IsReadOnly,
                                            TypeSymbol = jobEntityParameter.TypeSymbol,
                                            IsAspect = false
                                        });
                                        if (hasChangeFilter)
                                        {
                                            queryChangeFilterTypes.Add(new Query
                                            {
                                                IsReadOnly = jobEntityParameter.IsReadOnly,
                                                Type = QueryType.ChangeFilter,
                                                TypeSymbol = jobEntityParameter.TypeSymbol
                                            });
                                        }
                                        return jobEntityParameter;
                                    }

                                    Invalid = true;
                                    return null;
                                }

                                // Aspects
                                if (typeSymbol.IsAspect())
                                {
                                    var typeHandleName = m_HandlesDescription.GetOrCreateTypeHandleField(typeSymbol, parameterSymbol.IsReadOnly());
                                    var jobEntityParameter = new JobEntityParam_Aspect(parameterSymbol, typeHandleName);
                                    queryAllTypes.Add(new Query
                                    {
                                        IsReadOnly = jobEntityParameter.IsReadOnly,
                                        Type = QueryType.All,
                                        TypeSymbol = jobEntityParameter.TypeSymbol
                                    });
                                    m_AspectTypesInExecuteMethod.Add(new ParameterTypeInJobEntityExecuteMethod
                                    {
                                        IsReadOnly = jobEntityParameter.IsReadOnly,
                                        TypeSymbol = jobEntityParameter.TypeSymbol,
                                        IsAspect = true
                                    });
                                    return jobEntityParameter;
                                }

                                // Error handling
                                if (typeSymbol.InheritsFromInterface("Unity.Entities.IBufferElementData"))
                                {
                                    JobEntityGeneratorErrors.SGJE0012(this, parameterSymbol.Locations.Single(), typeSymbol.Name);
                                    Invalid = true;
                                    return null;
                                }

                                JobEntityGeneratorErrors.SGJE0003(this, parameterSymbol.Locations.Single(), parameterSymbol.Name, typeSymbol.ToFullName());
                                Invalid = true;
                                return new JobEntityParamValueTypesPassedWithDefaultArguments(parameterSymbol);
                            }

                            // Managed Components
                            var isUnityEngineComponent = JobEntityParam.IsUnityEngineComponent(typeSymbol);
                            if (isUnityEngineComponent != null)
                            {
                                var typeHandleName = m_HandlesDescription.GetOrCreateTypeHandleField(typeSymbol, parameterSymbol.IsReadOnly());
                                m_RequiresEntityManager = true;
                                var jobEntityParameter = new JobEntityParam_ManagedComponent(parameterSymbol, typeHandleName);
                                queryAllTypes.Add(new Query
                                {
                                    IsReadOnly = jobEntityParameter.IsReadOnly,
                                    Type = QueryType.All,
                                    TypeSymbol = jobEntityParameter.TypeSymbol
                                });
                                m_ComponentTypesInExecuteMethod.Add(new ParameterTypeInJobEntityExecuteMethod
                                {
                                    IsReadOnly = jobEntityParameter.IsReadOnly,
                                    TypeSymbol = jobEntityParameter.TypeSymbol,
                                    IsAspect = false
                                });
                                if (hasChangeFilter)
                                {
                                    queryChangeFilterTypes.Add(new Query
                                    {
                                        IsReadOnly = jobEntityParameter.IsReadOnly,
                                        Type = QueryType.ChangeFilter,
                                        TypeSymbol = jobEntityParameter.TypeSymbol
                                    });
                                }
                                return jobEntityParameter;
                            }

                            JobEntityGeneratorErrors.SGJE0010(this, parameterSymbol.Locations.Single(), parameterSymbol.Name, typeSymbol.ToFullName());
                            Invalid = true;
                            return null;
                        }
                        default:
                        {
                            Invalid = true;
                            return null;
                        }
                    }
                }
            }
            Invalid = true;
            return null;
        }

        ref struct QueryInfo
        {
            public List<Query> QueryAllTypes;
            public List<Query> QueryAnyTypes;
            public List<Query> QueryNoneTypes;
            public List<Query> QueryDisabledTypes;
            public List<Query> QueryAbsentTypes;
            public List<Query> QueryChangeFilterTypes;
            public EntityQueryOptions EntityQueryOptions;

            public static QueryInfo Default() =>
                new QueryInfo
                {
                    QueryAllTypes = new List<Query>(),
                    QueryAnyTypes = new List<Query>(),
                    QueryNoneTypes = new List<Query>(),
                    QueryDisabledTypes = new List<Query>(),
                    QueryAbsentTypes = new List<Query>(),
                    QueryChangeFilterTypes = new List<Query>(),
                    EntityQueryOptions = EntityQueryOptions.Default,
                };

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static ITypeSymbol AddFromTypeOfKeepAll(List<Query> list, TypeOfExpressionSyntax typeOfSyntax, QueryType type, SemanticModel semanticModel)
            {
                var typeSymbol = semanticModel.GetTypeInfo(typeOfSyntax.Type).Type;
                list.Add(new Query
                {
                    TypeSymbol = typeSymbol,
                    Type = type,
                    IsReadOnly = true,
                });
                return typeSymbol;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void AddFromTypeOfRemoveFromAll(List<Query> list, TypeOfExpressionSyntax typeOfSyntax, QueryType type, SemanticModel semanticModel)
            {
                var typeSymbol = AddFromTypeOfKeepAll(list, typeOfSyntax, type, semanticModel);
                var index = QueryAllTypes.FindIndex(q => SymbolEqualityComparer.Default.Equals(q.TypeSymbol, typeSymbol));
                if (index != -1)
                    QueryAllTypes.RemoveAtSwapBack(index);
            }

            static void AddToQueryListKeepAll(List<Query> list, AttributeArgumentSyntax argument,  QueryType type, SemanticModel semanticModel)
            {
                switch (argument.Expression)
                {
                    case ImplicitArrayCreationExpressionSyntax implicitArraySyntax:
                        foreach (var expression in implicitArraySyntax.Initializer.Expressions)
                            if (expression is TypeOfExpressionSyntax typeOfSyntax)
                                AddFromTypeOfKeepAll(list, typeOfSyntax, type, semanticModel);
                        break;
                    case ArrayCreationExpressionSyntax arraySyntax:
                        foreach (var expression in arraySyntax.Initializer.Expressions)
                            if (expression is TypeOfExpressionSyntax typeOfSyntax)
                                AddFromTypeOfKeepAll(list, typeOfSyntax, type, semanticModel);
                        break;
                    case TypeOfExpressionSyntax typeOfSyntax:
                        AddFromTypeOfKeepAll(list, typeOfSyntax, type, semanticModel);
                        break;
                }
            }

            void AddToQueryListRemoveFromAll(List<Query> list, AttributeArgumentSyntax argument,  QueryType type, SemanticModel semanticModel)
            {
                switch (argument.Expression)
                {
                    case ImplicitArrayCreationExpressionSyntax implicitArraySyntax:
                        foreach (var expression in implicitArraySyntax.Initializer.Expressions)
                            if (expression is TypeOfExpressionSyntax typeOfSyntax)
                                AddFromTypeOfRemoveFromAll(list, typeOfSyntax, type, semanticModel);
                        break;
                    case ArrayCreationExpressionSyntax arraySyntax:
                        foreach (var expression in arraySyntax.Initializer.Expressions)
                            if (expression is TypeOfExpressionSyntax typeOfSyntax)
                                AddFromTypeOfRemoveFromAll(list, typeOfSyntax, type, semanticModel);
                        break;
                    case TypeOfExpressionSyntax typeOfSyntax:
                        AddFromTypeOfRemoveFromAll(list, typeOfSyntax, type, semanticModel);
                        break;
                }
            }

            /// <summary>
            /// Fills <see cref="QueryAllTypes"/>, <see cref="QueryAnyTypes"/>,
            /// <see cref="QueryNoneTypes"/>, <see cref="QueryChangeFilterTypes"/>
            /// and <see cref="EntityQueryOptions"/> using C# attributes found on the JobEntity struct.
            /// </summary>
            public void FillFromAttributes(SyntaxList<AttributeListSyntax> attributeLists, SemanticModel semanticModel)
            {
                // Attributes contain multiple lists
                // [MyAttributeA, MyAttributeB] [MyAttributeC] [MyAttributeD]
                foreach (var attributeList in attributeLists)
                {
                    // With multiple attributes
                    // [MyAttributeA, MyAttributeB]
                    foreach (var attribute in attributeList.Attributes)
                    {
                        switch (attribute.Name)
                        {
                            case SimpleNameSyntax { Identifier: { ValueText: "WithDisabled" } }:
                                if (attribute.ArgumentList != null)
                                    foreach (var argument in attribute.ArgumentList.Arguments)
                                        AddToQueryListRemoveFromAll(QueryDisabledTypes, argument, QueryType.Disabled, semanticModel);
                                break;
                            case SimpleNameSyntax { Identifier: { ValueText: "WithAbsent" } }:
                                if (attribute.ArgumentList != null)
                                    foreach (var argument in attribute.ArgumentList.Arguments)
                                        AddToQueryListKeepAll(QueryAbsentTypes, argument, QueryType.Absent, semanticModel);
                                break;
                            case SimpleNameSyntax { Identifier: { ValueText: "WithAll" } }:
                                if (attribute.ArgumentList != null)
                                    foreach (var argument in attribute.ArgumentList.Arguments)
                                        AddToQueryListKeepAll(QueryAllTypes, argument, QueryType.All, semanticModel);
                                break;
                            case SimpleNameSyntax { Identifier: { ValueText: "WithNone" } }:
                                if (attribute.ArgumentList != null)
                                    foreach (var argument in attribute.ArgumentList.Arguments)
                                        AddToQueryListKeepAll(QueryNoneTypes, argument, QueryType.None, semanticModel);
                                break;

                            case SimpleNameSyntax { Identifier: { ValueText: "WithAny" } }:
                                if (attribute.ArgumentList != null)
                                    foreach (var argument in attribute.ArgumentList.Arguments)
                                        AddToQueryListKeepAll(QueryAnyTypes, argument, QueryType.Any, semanticModel);
                                break;

                            case SimpleNameSyntax { Identifier: { ValueText: "WithChangeFilter" } }:
                                if (attribute.ArgumentList != null)
                                    foreach (var argument in attribute.ArgumentList.Arguments)
                                        AddToQueryListKeepAll(QueryChangeFilterTypes, argument, QueryType.ChangeFilter, semanticModel);
                                break;

                            case SimpleNameSyntax { Identifier: { ValueText: "WithOptions" } }:
                                if (attribute.ArgumentList != null)
                                    foreach (var argument in attribute.ArgumentList.Arguments)
                                        AddEntityQueryOption(argument.Expression);
                                break;
                        }
                    }
                }
            }

            void AddEntityQueryOption(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    // if bitwise or
                    case BinaryExpressionSyntax binaryExpression
                        when binaryExpression.IsKind(SyntaxKind.BitwiseOrExpression):
                        if (binaryExpression.Right is MemberAccessExpressionSyntax memberAccess)
                            EntityQueryOptions |= memberAccess.GetEntityQueryOptions();
                        AddEntityQueryOption(binaryExpression.Left);
                        break;
                    case MemberAccessExpressionSyntax memberAccessSyntax:
                        EntityQueryOptions |= memberAccessSyntax.GetEntityQueryOptions();
                        break;
                }
            }

            public void CreateDefaultQuery(ref HandlesDescription handlesDescription)
            {
                handlesDescription.GetOrCreateQueryField(
                    new SingleArchetypeQueryFieldDescription(
                        new Archetype(QueryAllTypes,
                            QueryAnyTypes,
                            QueryNoneTypes,
                            QueryDisabledTypes,
                            QueryAbsentTypes,
                            options: EntityQueryOptions),
                        changeFilterTypes: QueryChangeFilterTypes), "DefaultQuery");
            }
        }

        /// <summary>
        /// Checks if any errors are present, if so, also outputs the error.
        /// </summary>
        /// <param name="systemTypeGeneratorContext"></param>
        /// <returns>True if error found</returns>
        void OutputErrors(IMethodSymbol userExecuteMethod)
        {
            var foundChunkIndexInQuery = false;
            var foundEntityIndexInQuery = false;
            var foundEntityIndexInChunk = false;

            // Check to see if our job has any generic type parameters
            if (m_JobEntityTypeSymbol.TypeParameters.Any())
            {
                JobEntityGeneratorErrors.SGJE0020(this, m_JobEntityTypeSymbol.Locations.First(), FullTypeName);
                Invalid = true;
            }

// TODO: This was recently fixed (https://github.com/dotnet/roslyn-analyzers/issues/5804), remove pragmas after we update .net
#pragma warning disable RS1024
            var alreadySeenComponents = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
#pragma warning restore RS1024

            foreach (var param in UserExecuteMethodParams)
            {
                if (param is null)
                    continue;

                if (!(param is IAttributeParameter { IsInt: true }))
                    if (!alreadySeenComponents.Add(param.TypeSymbol))
                    {
                        JobEntityGeneratorErrors.SGJE0017(this, param.ParameterSymbol.Locations.First(), FullTypeName, param.TypeSymbol.ToDisplayString());
                        Invalid = true;
                    }

                switch (param)
                {
                    case IAttributeParameter attributeParameter:
                    {
                        if (attributeParameter.IsInt) {
                            switch (attributeParameter)
                            {
                                case JobEntityParam_ChunkIndexInQuery _ when !foundChunkIndexInQuery:
                                    foundChunkIndexInQuery = true;
                                    continue;
                                case JobEntityParam_EntityIndexInQuery _ when !foundEntityIndexInQuery:
                                    foundEntityIndexInQuery = true;
                                    continue;
                                case JobEntityParam_EntityIndexInChunk _ when !foundEntityIndexInChunk:
                                    foundEntityIndexInChunk = true;
                                    continue;
                            }

                            JobEntityGeneratorErrors.SGJE0007(this, param.ParameterSymbol.Locations.Single(),
                                FullTypeName, GetUserExecuteMethodSignature(userExecuteMethod), attributeParameter.AttributeName);
                            Invalid = true;
                            continue;
                        }

                        JobEntityGeneratorErrors.SGJE0006(this, param.ParameterSymbol.Locations.Single(),
                            FullTypeName, GetUserExecuteMethodSignature(userExecuteMethod), param.ParameterSymbol.Name, attributeParameter.AttributeName);
                        Invalid = true;
                        continue;
                    }
                    case JobEntityParam_SharedComponent sharedComponent:
                        if (sharedComponent.ParameterSymbol.RefKind == RefKind.Ref)
                        {
                            var text = sharedComponent.ParameterSymbol.DeclaringSyntaxReferences.First().GetSyntax() is ParameterSyntax {Identifier: var i}
                                ? i.ValueText
                                : sharedComponent.ParameterSymbol.ToDisplayString();
                                JobEntityGeneratorErrors.SGJE0013(this, sharedComponent.ParameterSymbol.Locations.Single(), FullTypeName, text);
                            Invalid = true;
                        }
                        break;
                    case JobEntityParam_ComponentData componentData:
                    {
                        // E.g. Execute(in RefRW<T1> t1, ref EnabledRefRO<T2> t2)
                        if (componentData.RefWrapperType != RefWrapperType.None
                            && componentData.ParameterSymbol.RefKind != RefKind.None)
                        {
                            JobEntityGeneratorErrors.SGJE0018(this,componentData.ParameterSymbol.Locations.Single());
                            Invalid = true;
                        }

                        // E.g. Execute(ref TagComponent tag)
                        else if (componentData.RefWrapperType == RefWrapperType.None
                                 && componentData.IsZeroSizedComponent
                                 && componentData.ParameterSymbol.RefKind == RefKind.Ref)
                        {
                            JobEntityGeneratorErrors.SGJE0016(
                                this,
                                componentData.ParameterSymbol.Locations.Single(),
                                FullTypeName,
                                componentData.ParameterSymbol.ToDisplayString());
                            Invalid = true;
                        }
                        break;
                    }
                }
            }
        }
    }

    public class JobEntityParam_SharedComponent : JobEntityParam
    {
        internal JobEntityParam_SharedComponent(IParameterSymbol parameterSymbol, string typeHandleFieldName) : base(parameterSymbol, typeHandleFieldName)
        {
            var typeName = TypeSymbol.Name;
            var variableName = $"{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}Data";
            VariableDeclarationAtStartOfExecuteMethod = TypeSymbol.IsUnmanagedType
                ? $"var {variableName} = chunk.GetSharedComponent(__TypeHandle.{TypeHandleFieldName});"
                : $"var {variableName} = chunk.GetSharedComponentManaged(__TypeHandle.{TypeHandleFieldName}, __EntityManager);";

            ExecuteMethodArgumentValue = parameterSymbol.RefKind == RefKind.In ? $"in {variableName}" : variableName;
        }
    }

    public class JobEntityParam_Entity : JobEntityParam
    {
        internal JobEntityParam_Entity(IParameterSymbol parameterSymbol, string typeHandleFieldName) : base(parameterSymbol, typeHandleFieldName)
        {
            const string entityArrayPointer = "entityPointer";
            VariableDeclarationAtStartOfExecuteMethod = $@"var {entityArrayPointer} = InternalCompilerInterface.UnsafeGetChunkEntityArrayIntPtr(chunk, __TypeHandle.{TypeHandleFieldName});";

            const string argumentInExecuteMethod = "entity";
            ExecuteMethodArgumentSetup = $"var {argumentInExecuteMethod} = InternalCompilerInterface.UnsafeGetCopyOfNativeArrayPtrElement<Entity>({entityArrayPointer}, entityIndexInChunk);";

            ExecuteMethodArgumentValue = argumentInExecuteMethod;
        }
    }

    public class JobEntityParam_DynamicBuffer : JobEntityParam
    {
        internal JobEntityParam_DynamicBuffer(IParameterSymbol parameterSymbol, ITypeSymbol typeArgSymbol, string typeHandleFieldName) : base(parameterSymbol, typeHandleFieldName)
        {
            TypeSymbol = typeArgSymbol;

            var bufferAccessorVariableName = $"{parameterSymbol.Name}BufferAccessor";
            VariableDeclarationAtStartOfExecuteMethod = $"var {bufferAccessorVariableName} = chunk.GetBufferAccessor(ref __TypeHandle.{TypeHandleFieldName});";

            var executeArgumentName = $"retrievedByIndexIn{bufferAccessorVariableName}";
            ExecuteMethodArgumentSetup = $"var {executeArgumentName} = {bufferAccessorVariableName}[entityIndexInChunk];";

            ExecuteMethodArgumentValue = parameterSymbol.RefKind switch
            {
                RefKind.Ref => $"ref {executeArgumentName}",
                RefKind.In => $"in {executeArgumentName}",
                _ => executeArgumentName
            };
        }
    }

    public class JobEntityParam_Aspect : JobEntityParam
    {
        internal JobEntityParam_Aspect(IParameterSymbol parameterSymbol, string typeHandleFieldName) : base(parameterSymbol, typeHandleFieldName)
        {
            // Per chunk
            var variableName = $"{TypeHandleFieldName}Array";
            VariableDeclarationAtStartOfExecuteMethod = $"var {variableName} = __TypeHandle.{TypeHandleFieldName}.Resolve(chunk);";

            // Per entity
            var executeMethodArgument = $"{variableName}Array";
            ExecuteMethodArgumentSetup = $"var {executeMethodArgument} = {variableName}[entityIndexInChunk];";

            ExecuteMethodArgumentValue = parameterSymbol.RefKind switch
            {
                RefKind.Ref => $"ref {executeMethodArgument}",
                RefKind.In => $"in {executeMethodArgument}",
                _ => executeMethodArgument
            };
        }
    }

    public class JobEntityParam_ManagedComponent : JobEntityParam
    {
        internal JobEntityParam_ManagedComponent(IParameterSymbol parameterSymbol, string typeHandleFieldName) : base(parameterSymbol, typeHandleFieldName)
        {
            var accessorVariableName = $"{parameterSymbol.Name}ManagedComponentAccessor";
            VariableDeclarationAtStartOfExecuteMethod = $"var {accessorVariableName} = chunk.GetManagedComponentAccessor(ref __TypeHandle.{TypeHandleFieldName}, __EntityManager);";

            var localName = ExecuteMethodArgumentValue = $"retrievedByIndexIn{accessorVariableName}";
            ExecuteMethodArgumentSetup = $"var {localName} = {accessorVariableName}[entityIndexInChunk];";

            // We do not allow managed components to be used by ref. See SourceGenerationErrors.DC0024.
            if(parameterSymbol.RefKind == RefKind.In)
                ExecuteMethodArgumentValue = $"in {localName}";
        }
    }

    public class JobEntityParam_ComponentData : JobEntityParam
    {
        internal JobEntityParam_ComponentData(
            IParameterSymbol parameterSymbol,
            ITypeSymbol componentTypeSymbol,
            bool isReadOnly,
            RefWrapperType refWrapperType,
            bool performSafetyChecks, string typeHandleFieldName) : base(parameterSymbol, typeHandleFieldName)
        {
            TypeSymbol = componentTypeSymbol;
            IsReadOnly = isReadOnly;
            RefWrapperType = refWrapperType;
            IsZeroSizedComponent = componentTypeSymbol.IsZeroSizedComponent();
            IsEnableableComponent = componentTypeSymbol.IsEnableableComponent();

            string typeName = componentTypeSymbol.Name;
            string fullyQualifiedTypeName = componentTypeSymbol.ToFullName();

            var executeMethodArg = GetIJobEntityExecuteMethodArgument();

            VariableDeclarationAtStartOfExecuteMethod = executeMethodArg.RequiredVariableDeclaration;
            ExecuteMethodArgumentSetup = executeMethodArg.SetUp;
            ExecuteMethodArgumentValue = executeMethodArg.Value;

            (string RequiredVariableDeclaration, string SetUp, string Value) GetIJobEntityExecuteMethodArgument()
            {
                string requiredVariableName;
                string requiredVariableDeclaration;
                string setUp;
                string value;

                switch (refWrapperType)
                {
                    case RefWrapperType.None:
                    {
                        requiredVariableName =
                            IsZeroSizedComponent
                                ? string.Empty
                                : $"{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}IntPtr";
                        requiredVariableDeclaration =
                            IsZeroSizedComponent
                                ? string.Empty
                                : IsReadOnly
                                    ? $"var {requiredVariableName} = InternalCompilerInterface.UnsafeGetChunkNativeArrayReadOnlyIntPtr<{fullyQualifiedTypeName}>(chunk, ref __TypeHandle.{TypeHandleFieldName});"
                                    : $"var {requiredVariableName} = InternalCompilerInterface.UnsafeGetChunkNativeArrayIntPtr<{fullyQualifiedTypeName}>(chunk, ref __TypeHandle.{TypeHandleFieldName});";

                        value = $"{requiredVariableName}Ref";
                        setUp =
                            IsZeroSizedComponent
                                ? string.Empty
                                : $"ref var {value} = ref InternalCompilerInterface.UnsafeGetRefToNativeArrayPtrElement<{fullyQualifiedTypeName}>({requiredVariableName}, entityIndexInChunk);";

                        value = ParameterSymbol.RefKind switch
                        {
                            RefKind.Ref => IsZeroSizedComponent ? "default" : $"ref {value}",
                            RefKind.In => IsZeroSizedComponent ? "default" : $"in {value}",
                            _ => IsZeroSizedComponent ? "default" : value
                        };
                        return (requiredVariableDeclaration, setUp, value);
                    }
                    case RefWrapperType.RefRW:
                    {
                        requiredVariableName = $"{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}IntPtr";
                        requiredVariableDeclaration = $"var {requiredVariableName} = InternalCompilerInterface.UnsafeGetChunkNativeArrayIntPtr<{fullyQualifiedTypeName}>(chunk, ref __TypeHandle.{TypeHandleFieldName});";

                        value = $"{requiredVariableName}Ref";
                        setUp =
                            performSafetyChecks
                                ? $"var {value} = Unity.Entities.InternalCompilerInterface.GetRefRW<{fullyQualifiedTypeName}>({requiredVariableName}, entityIndexInChunk, ref __TypeHandle.{TypeHandleFieldName});"
                                : $"var {value} = Unity.Entities.InternalCompilerInterface.GetRefRW<{fullyQualifiedTypeName}>({requiredVariableName}, entityIndexInChunk);";
                        return (requiredVariableDeclaration, setUp, value);
                    }
                    case RefWrapperType.RefRO:
                    {
                        requiredVariableName = $"{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}ArrayIntPtr";
                        requiredVariableDeclaration = $"var {requiredVariableName} = InternalCompilerInterface.UnsafeGetChunkNativeArrayReadOnlyIntPtr<{fullyQualifiedTypeName}>(chunk, ref __TypeHandle.{TypeHandleFieldName});";

                        value = $"{requiredVariableName}Ref";
                        setUp =
                            performSafetyChecks
                                ? $"var {value} = Unity.Entities.InternalCompilerInterface.GetRefRO<{fullyQualifiedTypeName}>({requiredVariableName}, entityIndexInChunk, ref __TypeHandle.{TypeHandleFieldName});"
                                : $"var {value} = Unity.Entities.InternalCompilerInterface.GetRefRO<{fullyQualifiedTypeName}>({requiredVariableName}, entityIndexInChunk);";
                        return (requiredVariableDeclaration, setUp, value);
                    }
                    case RefWrapperType.EnabledRefRO:
                    case RefWrapperType.EnabledRefRW:
                    {
                        requiredVariableName = $"{char.ToLowerInvariant(typeName[0])}{typeName.Substring(1)}EnabledMask_{(IsReadOnly ? "RO" : "RW")}";
                        requiredVariableDeclaration = $"var {requiredVariableName} = chunk.GetEnabledMask(ref __TypeHandle.{TypeHandleFieldName});";

                        value = $"{requiredVariableName}.{(IsReadOnly ? "GetEnabledRefRO" : "GetEnabledRefRW")}<{fullyQualifiedTypeName}>(entityIndexInChunk)";
                        return (requiredVariableDeclaration, SetUp: default, value);
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
        public RefWrapperType RefWrapperType { get; }
        public bool IsZeroSizedComponent { get; }
        public bool IsEnableableComponent { get; }
    }
    public interface IAttributeParameter
    {
        public bool IsInt { get; }
        public string AttributeName { get; }
    }

    class JobEntityParamValueTypesPassedWithDefaultArguments : JobEntityParam
    {
        internal JobEntityParamValueTypesPassedWithDefaultArguments(IParameterSymbol parameterSymbol) : base(parameterSymbol, "")
        {
            ExecuteMethodArgumentValue = "default";
        }
    }

    class JobEntityParam_EntityIndexInQuery : JobEntityParam, IAttributeParameter
    {
        public bool IsInt => TypeSymbol.IsInt();
        public string AttributeName => "EntityIndexInQuery";

        internal JobEntityParam_EntityIndexInQuery(IParameterSymbol parameterSymbol) : base(parameterSymbol, string.Empty)
        {
            ExecuteMethodArgumentSetup = " var entityIndexInQuery = __ChunkBaseEntityIndices[chunkIndexInQuery] + matchingEntityCount;";
            ExecuteMethodArgumentValue = "entityIndexInQuery";
        }
    }

    class JobEntityParam_ChunkIndexInQuery : JobEntityParam, IAttributeParameter
    {
        public bool IsInt => TypeSymbol.IsInt();
        public string AttributeName => "ChunkIndexInQuery";
        internal JobEntityParam_ChunkIndexInQuery(IParameterSymbol parameterSymbol) : base(parameterSymbol, string.Empty)
        {
            // TODO(DOTS-6130): an extra helper job is needed to provided the chunk index in query when the query has chunk filtering enabled.
            // For now this is an unfiltered chunk index.
            ExecuteMethodArgumentValue = "chunkIndexInQuery";
        }
    }

    class JobEntityParam_EntityIndexInChunk : JobEntityParam, IAttributeParameter
    {
        public bool IsInt => TypeSymbol.IsInt();
        public string AttributeName => "EntityIndexInChunk";
        internal JobEntityParam_EntityIndexInChunk(IParameterSymbol parameterSymbol) : base(parameterSymbol, string.Empty)
        {
            ExecuteMethodArgumentValue = "entityIndexInChunk";
        }
    }

    public abstract class JobEntityParam
    {
        public bool RequiresExecuteMethodArgumentSetup => !string.IsNullOrEmpty(ExecuteMethodArgumentSetup);
        public string ExecuteMethodArgumentSetup { get; protected set; }
        public string ExecuteMethodArgumentValue { get; protected set; }
        public IParameterSymbol ParameterSymbol { get; }
        public ITypeSymbol TypeSymbol { get; protected set; }

        public string TypeHandleFieldName { get; }

        public bool IsReadOnly { get; protected set; }
        public string VariableDeclarationAtStartOfExecuteMethod { get; protected set; }

        internal static (bool Success, JobEntityParam JobEntityParameter) TryParseComponentTypeSymbol(
            ITypeSymbol componentTypeSymbol,
            IParameterSymbol parameterSymbol,
            bool isReadOnly,
            bool performSafetyChecks,
            ISourceGeneratorDiagnosable diagnosable,
            string typeHandleFieldName,
            string constructedFrom = null)
        {
            var refWrapperType = constructedFrom switch
            {
                "global::Unity.Entities.RefRW<T>" => RefWrapperType.RefRW,
                "global::Unity.Entities.RefRO<T>" => RefWrapperType.RefRO,
                "global::Unity.Entities.EnabledRefRW<T>" => RefWrapperType.EnabledRefRW,
                "global::Unity.Entities.EnabledRefRO<T>" => RefWrapperType.EnabledRefRO,
                _ => RefWrapperType.None
            };

            if (componentTypeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.Arity != 0)
            {
                JobEntityGeneratorErrors.SGJE0011(diagnosable, parameterSymbol.Locations.Single(), parameterSymbol.Name);
                return (false, default);
            }
            return (true, new JobEntityParam_ComponentData(parameterSymbol, componentTypeSymbol, isReadOnly, refWrapperType, performSafetyChecks, typeHandleFieldName));
        }

        internal static bool? IsUnityEngineComponent(ITypeSymbol typeSymbol)
        {
            return
                typeSymbol.InheritsFromInterface("Unity.Entities.IComponentData")
                    ? false
                    : typeSymbol.InheritsFromType("UnityEngine.Component")
                      || typeSymbol.InheritsFromType("UnityEngine.GameObject")
                      || typeSymbol.InheritsFromType("UnityEngine.ScriptableObject")
                        ? (bool?)true
                        : null;
        }

        internal JobEntityParam(IParameterSymbol parameterSymbol, string typeHandleFieldName)
        {
            ParameterSymbol = parameterSymbol;
            TypeSymbol = parameterSymbol.Type;
            TypeHandleFieldName = typeHandleFieldName;
            IsReadOnly = parameterSymbol.IsReadOnly();
        }
    }
}