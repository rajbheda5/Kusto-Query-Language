﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kusto.Language.Binding
{
    using Parsing;
    using Symbols;
    using Syntax;
    using Utils;

    /// <summary>
    /// The kinds of conversions allowed between values of two different types.
    /// </summary>
    internal enum Conversion
    {
        /// <summary>
        /// No conversion allowed between different scalar types (strict)
        /// </summary>
        None,

        /// <summary>
        /// Type promotion (widening) allowed.
        /// </summary>
        Promotable,

        /// <summary>
        /// Conversions between compatible types allowed (widening or narrowing)
        /// </summary>
        Compatible,

        /// <summary>
        /// All conversions allowed (no checking)
        /// </summary>
        Any
    }

    /// <summary>
    /// Binding state that persists across multiple bindings (lifetime of <see cref="KustoCache"/>)
    /// </summary>
    internal class GlobalBindingCache
    {
        internal readonly Dictionary<IReadOnlyList<TableSymbol>, TableSymbol> UnifiedNameColumnsMap =
            new Dictionary<IReadOnlyList<TableSymbol>, TableSymbol>(ReadOnlyListComparer<TableSymbol>.Default);

        internal readonly Dictionary<IReadOnlyList<TableSymbol>, TableSymbol> UnifiedNameAndTypeColumnsMap =
            new Dictionary<IReadOnlyList<TableSymbol>, TableSymbol>(ReadOnlyListComparer<TableSymbol>.Default);

        internal readonly Dictionary<IReadOnlyList<TableSymbol>, TableSymbol> CommonColumnsMap =
            new Dictionary<IReadOnlyList<TableSymbol>, TableSymbol>(ReadOnlyListComparer<TableSymbol>.Default);

        internal Dictionary<CallSiteInfo, Syntax.FunctionBody> CallSiteToExpansionMap =
            new Dictionary<CallSiteInfo, Syntax.FunctionBody>(CallSiteInfo.Comparer.Instance);
    }

    /// <summary>
    /// Binding state that exists for the duration of the binder.
    /// </summary>
    internal class LocalBindingCache
    {
        internal readonly HashSet<Signature> SignaturesComputingExpansion
            = new HashSet<Signature>();

        internal Dictionary<CallSiteInfo, Syntax.FunctionBody> CallSiteToExpansionMap =
            new Dictionary<CallSiteInfo, Syntax.FunctionBody>(CallSiteInfo.Comparer.Instance);
    }

    internal class CallSiteInfo
    {
        public Signature Signature { get; }

        public IReadOnlyList<VariableSymbol> Locals { get; }

        public CallSiteInfo(Signature signature, IReadOnlyList<VariableSymbol> locals)
        {
            this.Signature = signature;
            this.Locals = locals;
        }

        public override string ToString()
        {
            return Signature.Symbol.Name + "("
                + string.Join(",", this.Locals.Select(v => v.IsConstant && v.ConstantValue != null ? $"{v.Name}={v.ConstantValue}" : v.Name))
                + ")";
        }

        internal class Comparer : IEqualityComparer<CallSiteInfo>
        {
            public static readonly Comparer Instance = new Comparer();

            public bool Equals(CallSiteInfo x, CallSiteInfo y)
            {
                if (x.Signature != y.Signature)
                    return false;

                if (x.Locals.Count != y.Locals.Count)
                    return false;

                for (int i = 0; i < x.Locals.Count; i++)
                {
                    var lx = x.Locals[i];
                    var ly = y.Locals[i];

                    if (lx.Name != ly.Name
                        || lx.Type != ly.Type
                        || lx.IsConstant != ly.IsConstant
                        || !object.Equals(lx.ConstantValue, ly.ConstantValue))
                        return false;
                }

                return true;
            }

            public int GetHashCode(CallSiteInfo obj)
            {
                return obj.Signature.GetHashCode();
            }
        }
    }

    /// <summary>
    /// The binder performs general semantic analysis of the syntax tree.
    /// </summary>
    internal sealed partial class Binder
    {
        /// <summary>
        /// Global state including symbols declared in ambient database.
        /// </summary>
        private readonly GlobalState _globals;

        /// <summary>
        /// The cluster assumed when resolveing unqualified calls to database() 
        /// </summary>
        private ClusterSymbol _currentCluster;

        /// <summary>
        /// The database assumed when resolving unqualified references table/function names or calls to table()
        /// </summary>
        private DatabaseSymbol _currentDatabase;

        /// <summary>
        /// All symbol declared locally within the query appear in the local scope.
        /// These are symbols declared by let statements or the as query operator.
        /// Local scopes may be nested within other local scopes.
        /// </summary>
        private LocalScope _localScope;

        /// <summary>
        /// Columns accessible in piped query operators
        /// </summary>
        private TableSymbol _rowScope;

        /// <summary>
        /// Columns accessible from right side of join operator
        /// </summary>
        private TableSymbol _rightRowScope;

        /// <summary>
        /// Members accessible from left side of path/element expression
        /// </summary>
        private Symbol _pathScope;

        /// <summary>
        /// Implicit argument type used for invoke binding.
        /// </summary>
        private TypeSymbol _implicitArgumentType;

        /// <summary>
        /// The kind of scope in effect.
        /// </summary>
        private ScopeKind _scopeKind;

        /// <summary>
        /// Any aliased databases.
        /// </summary>
        private readonly Dictionary<string, DatabaseSymbol> _aliasedDatabases =
            new Dictionary<string, DatabaseSymbol>();

        /// <summary>
        /// Binding state that is shared across many binders/bindings
        /// </summary>
        private readonly GlobalBindingCache _globalBindingCache;

        /// <summary>
        /// Binding state that is private to one binding (including nested bindings)
        /// </summary>
        private readonly LocalBindingCache _localBindingCache;

        /// <summary>
        /// An optional function that assigns <see cref="SemanticInfo"/> to a <see cref="SyntaxNode"/>
        /// </summary>
        private readonly Action<SyntaxNode, SemanticInfo> _semanticInfoSetter;

        /// <summary>
        /// An optional <see cref="CancellationToken"/> specified for use during binding.
        /// </summary>
        private readonly CancellationToken _cancellationToken;

        private Binder(
            GlobalState globals,
            ClusterSymbol currentCluster,
            DatabaseSymbol currentDatabase,
            LocalScope outerScope,
            GlobalBindingCache globalBindingCache,
            LocalBindingCache localBindingCache,
            Action<SyntaxNode, SemanticInfo> semanticInfoSetter,
            CancellationToken cancellationToken)
        {
            _globals = globals;
            _currentCluster = currentCluster ?? globals.Cluster;
            _currentDatabase = currentDatabase ?? globals.Database;
            _globalBindingCache = globalBindingCache ?? new GlobalBindingCache();
            _localBindingCache = localBindingCache ?? new LocalBindingCache();
            _localScope = new LocalScope(outerScope);
            _semanticInfoSetter = semanticInfoSetter;
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Do semantic analysis over the syntax tree.
        /// </summary>
        public static void Bind(
            SyntaxNode root,
            GlobalState globals,
            LocalBindingCache localBindingCache = null,
            Action<SyntaxNode, SemanticInfo> semanticInfoSetter = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            semanticInfoSetter = semanticInfoSetter ?? DefaultSetSemanticInfo;

            var bindingCache = globals.Cache.GetOrCreate<GlobalBindingCache>();
            lock (bindingCache)
            {
                var binder = new Binder(
                    globals,
                    globals.Cluster,
                    globals.Database,
                    GetDefaultOuterScope(globals),
                    bindingCache,
                    localBindingCache,
                    semanticInfoSetter: semanticInfoSetter,
                    cancellationToken: cancellationToken);
                var treeBinder = new TreeBinder(binder);
                root.Accept(treeBinder);
            }
        }

        private static LocalScope GetDefaultOuterScope(GlobalState globals)
        {
            LocalScope outerScope = null;

            if (globals.Parameters.Count > 0)
            {
                outerScope = new LocalScope();
                outerScope.AddSymbols(globals.Parameters);
            }

            return outerScope;
        }

        private static void DefaultSetSemanticInfo(SyntaxNode node, SemanticInfo info)
        {
            if (info != null)
            {
                var data = node.GetExtendedData(create: true);
                data.SemanticInfo = info;
            }
        }

        /// <summary>
        /// Do semantic analysis over an inline expansion of a function body.
        /// </summary>
        public static void BindExpansion(
            SyntaxNode expansionRoot,
            Binder outer,
            ClusterSymbol currentCluster,
            DatabaseSymbol currentDatabase,
            LocalScope outerScope,
            IEnumerable<Symbol> locals)
        {
            var binder = new Binder(
                outer._globals,
                currentCluster ?? outer._currentCluster,
                currentDatabase ?? outer._currentDatabase,
                outerScope,
                outer._globalBindingCache,
                outer._localBindingCache,
                outer._semanticInfoSetter,
                outer._cancellationToken);

            if (locals != null)
            {
                binder.SetLocals(locals);
            }

            var treeBinder = new TreeBinder(binder);
            expansionRoot.Accept(treeBinder);
        }

        private void SetLocals(IEnumerable<Symbol> locals)
        {
            foreach (var local in locals)
            {
                _localScope.AddSymbol(local);
            }
        }

        /// <summary>
        /// Sets the context of the binder to the specified node and text position.
        /// </summary>
        private void SetContext(SyntaxNode contextNode, int position = -1)
        {
            // note: assumes this API is only called at most once after constructor.
            if (contextNode != null)
            {
                var builder = new ContextBuilder(this, position >= 0 ? position : contextNode.TextStart);
                contextNode.Accept(builder);
            }
        }       

        /// <summary>
        /// Gets the computed return type for functions specified with a body or declaration.
        /// </summary>
        public static TypeSymbol GetComputedReturnType(Signature signature, GlobalState globals)
        {
            var currentDatabase = globals.GetDatabase((FunctionSymbol)signature.Symbol);
            var currentCluster = globals.GetCluster(currentDatabase);

            var bindingCache = globals.Cache.GetOrCreate<GlobalBindingCache>();
            lock (bindingCache)
            {
                var binder = new Binder(
                    globals,
                    currentCluster,
                    currentDatabase,
                    GetDefaultOuterScope(globals),
                    bindingCache,
                    localBindingCache: null,
                    semanticInfoSetter: null, 
                    cancellationToken: default(CancellationToken));
                return binder.GetComputedSignatureResult(signature).Type;
            }
        }

        /// <summary>
        /// Scope kind.
        /// </summary>
        private enum ScopeKind
        {
            /// <summary>
            /// Normal lookup in <see cref="Binder"/>
            /// </summary>
            Normal,

            /// <summary>
            /// Only aggregate functions are visible
            /// </summary>
            Aggregate,

            /// <summary>
            /// Only plug-in funtions are visible
            /// </summary>
            PlugIn
        }

        /// <summary>
        /// Gets the <see cref="ScopeKind"/> in effect for a function's arguments.
        /// </summary>
        private ScopeKind GetArgumentScope(FunctionCallExpression fc, ScopeKind outerScope)
        {
            if (GetReferencedSymbol(fc.Name) is FunctionSymbol fs
                && _globals.IsAggregateFunction(fs))
            {
                // aggregate function arguments are always normal
                return ScopeKind.Normal;
            }
            else if (outerScope == ScopeKind.Aggregate)
            {
                // if the function is not a known aggregate then keep aggregate scope as there may be
                // aggregates nested in the function arguments
                return ScopeKind.Aggregate;
            }
            else
            {
                return ScopeKind.Normal;
            }
        }

        #region Semantic Info accessors
        private SemanticInfo GetSemanticInfo(SyntaxNode node)
        {
            return node?.GetSemanticInfo();
        }

        private void SetSemanticInfo(SyntaxNode node, SemanticInfo info)
        {
            if (node != null)
            {
                _semanticInfoSetter?.Invoke(node, info);
            }
        }

        private TypeSymbol GetResultTypeOrError(Expression expression) =>
            GetSemanticInfo(expression)?.ResultType ?? ErrorSymbol.Instance;

        private TypeSymbol GetResultType(Expression expression) =>
            GetSemanticInfo(expression)?.ResultType;

        private Symbol GetReferencedSymbol(Expression expression) =>
            GetSemanticInfo(expression)?.ReferencedSymbol;

        private bool GetIsConstant(Expression expression) =>
            GetSemanticInfo(expression)?.IsConstant ?? false;
        #endregion

        #region Symbol access/caching
        /// <summary>
        /// Gets the cluster for the specified name, or an empty open cluster.
        /// </summary>
        private ClusterSymbol GetCluster(string name)
        {
            var cluster = _globals.GetCluster(name);
            return cluster ?? GetOpenCluster(name);
        }

        private Dictionary<string, ClusterSymbol> _openClusters;

        private ClusterSymbol GetOpenCluster(string name)
        {
            if (_openClusters == null)
            {
                _openClusters = new Dictionary<string, ClusterSymbol>();
            }

            if (!_openClusters.TryGetValue(name, out var cluster))
            {
                cluster = new ClusterSymbol(name, null, isOpen: true);
                _openClusters.Add(name, cluster);
            }

            return cluster;
        }

        /// <summary>
        /// Gets the named database.
        /// </summary>
        private DatabaseSymbol GetDatabase(string name, ClusterSymbol cluster = null)
        {
            cluster = cluster ?? _currentCluster;

            if (cluster == _currentCluster && string.Compare(_currentDatabase.Name, name, ignoreCase: true) == 0)
            {
                return _currentDatabase;
            }

            if (_aliasedDatabases.TryGetValue(name, out var db))
            {
                return db;
            }

            var list = s_symbolListPool.AllocateFromPool();
            try
            {

                cluster.GetMembers(name, SymbolMatch.Database, list, ignoreCase: true);

                if (list.Count >= 1)
                {
                    return (DatabaseSymbol)list[0];
                }
                else if (cluster.IsOpen)
                {
                    return GetOpenDatabase(name, cluster);
                }
                else
                {
                    return null;
                }
            }
            finally
            {
                s_symbolListPool.ReturnToPool(list);
            }
        }

        private Dictionary<ClusterSymbol, Dictionary<string, DatabaseSymbol>> _openDatabases;

        private DatabaseSymbol GetOpenDatabase(string name, ClusterSymbol cluster)
        {
            cluster = cluster ?? _currentCluster;

            if (_openDatabases == null)
            {
                _openDatabases = new Dictionary<ClusterSymbol, Dictionary<string, DatabaseSymbol>>();
            }

            if (!_openDatabases.TryGetValue(cluster, out var map))
            {
                map = new Dictionary<string, DatabaseSymbol>();
                _openDatabases.Add(cluster, map);
            }

            if (!map.TryGetValue(name, out var database))
            {
                database = new DatabaseSymbol(name, null, isOpen: true);
                map.Add(name, database);
            }

            return database;
        }

        /// <summary>
        /// Gets the named table.
        /// </summary>
        private TableSymbol GetTable(string name, DatabaseSymbol database = null)
        {
            database = database ?? _currentDatabase;

            var list = s_symbolListPool.AllocateFromPool();
            try
            {
                database.GetMembers(name, SymbolMatch.Table, list);
                if (list.Count >= 1)
                {
                    return (TableSymbol)list[0];
                }
                else if (database.IsOpen)
                {
                    return GetOpenTable(name, database);
                }
                else
                {
                    return null;
                }
            }
            finally
            {
                s_symbolListPool.ReturnToPool(list);
            }
        }

        private Dictionary<DatabaseSymbol, Dictionary<string, TableSymbol>> _openTables;

        private TableSymbol GetOpenTable(string name, DatabaseSymbol database)
        {
            if (_openTables == null)
            {
                _openTables = new Dictionary<DatabaseSymbol, Dictionary<string, TableSymbol>>();
            }

            if (!_openTables.TryGetValue(database, out var map))
            {
                map = new Dictionary<string, TableSymbol>();
                _openTables.Add(database, map);
            }

            if (!map.TryGetValue(name, out var table))
            {
                table = new TableSymbol(name).Open();
                map.Add(name, table);
            }

            return table;
        }

        private Dictionary<TableSymbol, Dictionary<string, ColumnSymbol>> openColumns;

        private ColumnSymbol GetOpenColumn(string name, TypeSymbol type, TableSymbol table)
        {
            if (openColumns == null)
            {
                openColumns = new Dictionary<TableSymbol, Dictionary<string, ColumnSymbol>>();
            }

            if (!openColumns.TryGetValue(table, out var columnMap))
            {
                columnMap = new Dictionary<string, ColumnSymbol>();
                openColumns.Add(table, columnMap);
            }

            if (!columnMap.TryGetValue(name, out var column))
            {
                column = new ColumnSymbol(name, type);
                columnMap.Add(name, column);
            }

            return column;
        }

        private void GetDeclaredAndInferredColumns(TableSymbol table, List<ColumnSymbol> columns)
        {
            columns.AddRange(table.Columns);

            if (table.IsOpen && openColumns != null && openColumns.TryGetValue(table, out var columnMap))
            {
                columns.AddRange(columnMap.Values);
            }
        }

        public IReadOnlyList<ColumnSymbol> GetDeclaredAndInferredColumns(TableSymbol table)
        {
            if (table.IsOpen && openColumns != null && openColumns.ContainsKey(table))
            {
                var list = new List<ColumnSymbol>();
                GetDeclaredAndInferredColumns(table, list);
                return list;
            }
            else
            {
                return table.Columns;
            }
        }

        private Dictionary<TableSymbol, TupleSymbol> tupleMap;

        /// <summary>
        /// Gets a tuple with the same columns (declared and inferred) as the table.
        /// </summary>
        private TupleSymbol GetTuple(TableSymbol table)
        {
            if (tupleMap == null)
            {
                tupleMap = new Dictionary<TableSymbol, TupleSymbol>();
            }

            if (!tupleMap.TryGetValue(table, out var tuple))
            {
                tuple = new TupleSymbol(GetDeclaredAndInferredColumns(table));
                tupleMap.Add(table, tuple);
            }

            return tuple;
        }

        private bool CanCache(IReadOnlyList<TableSymbol> tables)
        {
            return tables == _currentDatabase.Tables || tables.All(t => _globals.IsDatabaseTable(t));
        }

        /// <summary>
        /// A table that contains all the columns in the specified list of tables, unified on name.
        /// </summary>
        private TableSymbol GetTableOfColumnsUnifiedByName(IReadOnlyList<TableSymbol> tables)
        {
            // consider making this cache thread safe
            if (!_globalBindingCache.UnifiedNameColumnsMap.TryGetValue(tables, out var unifiedColumnsTable))
            {
                var cache = CanCache(tables);

                tables = tables.ToReadOnly();
                var columns = new List<ColumnSymbol>();

                foreach (var table in tables)
                {
                    columns.AddRange(table.Columns);
                }

                Binder.UnifyColumnsWithSameName(columns);

                unifiedColumnsTable = new TableSymbol(columns);

                if (cache)
                {
                    _globalBindingCache.UnifiedNameColumnsMap[tables] = unifiedColumnsTable;
                }
            }

            return unifiedColumnsTable;
        }

        /// <summary>
        /// A table that contains all the columns in the specified list of tables, unified on name and type.
        /// </summary>
        private TableSymbol GetTableOfColumnsUnifiedByNameAndType(IReadOnlyList<TableSymbol> tables)
        {
            // consider making this cache thread safe
            if (!_globalBindingCache.UnifiedNameAndTypeColumnsMap.TryGetValue(tables, out var unifiedColumnsTable))
            {
                var cache = CanCache(tables);

                tables = tables.ToReadOnly();
                var columns = new List<ColumnSymbol>();

                foreach (var table in tables)
                {
                    columns.AddRange(table.Columns);
                }

                Binder.UnifyColumnsWithSameNameAndType(columns);

                unifiedColumnsTable = new TableSymbol(columns);

                if (cache)
                {
                    _globalBindingCache.UnifiedNameAndTypeColumnsMap[tables] = unifiedColumnsTable;
                }
            }

            return unifiedColumnsTable;
        }

        /// <summary>
        /// A table that contains the common columns in the specified list of tables.
        /// </summary>
        private TableSymbol GetTableOfCommonColumns(IReadOnlyList<TableSymbol> tables)
        {
            // consider making this cache thread safe
            if (!_globalBindingCache.CommonColumnsMap.TryGetValue(tables, out var commonColumnsTable))
            {
                var cache = CanCache(tables);

                tables = tables.ToReadOnly();
                var columns = new List<ColumnSymbol>();

                Binder.GetCommonColumns(tables, columns);

                commonColumnsTable = new TableSymbol(columns);

                if (cache)
                {
                    _globalBindingCache.CommonColumnsMap[tables] = commonColumnsTable;
                }
            }

            return commonColumnsTable;
        }
        #endregion

        #region Symbols in scope
        /// <summary>
        /// Gets all the symbols that are in scope at the text position.
        /// </summary>
        public static void GetSymbolsInScope(SyntaxNode root, int position, GlobalState globals, SymbolMatch match, IncludeFunctionKind include, List<Symbol> list, CancellationToken cancellationToken)
        {
            var bindingCache = globals.Cache.GetOrCreate<GlobalBindingCache>();
            lock (bindingCache)
            {
                var binder = new Binder(
                    globals,
                    globals.Cluster,
                    globals.Database,
                    GetDefaultOuterScope(globals),
                    bindingCache,
                    localBindingCache: null,
                    semanticInfoSetter: null,
                    cancellationToken: cancellationToken);
                var startNode = GetStartNode(root, position);
                binder.SetContext(startNode, position);
                binder.GetSymbolsInContext(startNode, match, include, list);
            }
        }

        /// <summary>
        /// Gets the <see cref="TableSymbol"/> that is in scope as the implicit set of columns accessible within a query.
        /// </summary>
        public static TableSymbol GetRowScope(SyntaxNode root, int position, GlobalState globals, CancellationToken cancellationToken = default(CancellationToken))
        {
            var bindingCache = globals.Cache.GetOrCreate<GlobalBindingCache>();
            lock (bindingCache)
            {
                var binder = new Binder(
                    globals,
                    globals.Cluster,
                    globals.Database,
                    GetDefaultOuterScope(globals),
                    bindingCache,
                    localBindingCache: null,
                    semanticInfoSetter: null,
                    cancellationToken: cancellationToken);
                var startNode = GetStartNode(root, position);
                binder.SetContext(startNode, position);
                return binder._rowScope;
            }
        }

        private static SyntaxNode GetStartNode(SyntaxNode root, int position)
        {
            var token = root.GetTokenAt(position);

            if (token != null && position <= token.TextStart)
            {
                var prev = token.GetPreviousToken();
                if (prev != null && prev.Depth >= token.Depth)
                {
                    return prev.Parent;
                }

                return token.Parent;
            }

            return null;
        }

        private void GetSymbolsInContext(SyntaxNode contextNode, SymbolMatch match, IncludeFunctionKind include, List<Symbol> list)
        {
            if (_pathScope != null)
            {
                // so far only columns, tables and functions can be dot accessed.
                var memberMatch = match & (SymbolMatch.Column | SymbolMatch.Table | SymbolMatch.Function);

                // table.column only works in commands
                if (_pathScope is TableSymbol && !IsInCommand(contextNode))
                {
                    memberMatch &= ~SymbolMatch.Column;
                }

                // any columns or tables from left-hand side?
                if (memberMatch != 0)
                {
                    _pathScope.GetMembers(memberMatch, list);
                }

                // any special functions from left-hand side?
                if ((match & SymbolMatch.Function) != 0)
                {
                    GetSpecialFunctions(null, list);
                }
            }
            else
            {
                switch (_scopeKind)
                {
                    case ScopeKind.Normal:
                        // row scope columns
                        if (_rowScope != null && (match & SymbolMatch.Column) != 0)
                        {
                            if (_rightRowScope != null)
                            {
                                // add $left and $right variables
                                list.Add(new VariableSymbol("$left", GetTuple(_rowScope)));
                                list.Add(new VariableSymbol("$right", GetTuple(_rightRowScope)));

                                // common columns
                                GetCommonColumns(GetDeclaredAndInferredColumns(_rowScope), GetDeclaredAndInferredColumns(_rightRowScope), list);
                            }
                            else
                            {
                                _rowScope.GetMembers(match, list);
                            }
                        }

                        // local symbols
                        _localScope.GetSymbols(match, list);

                        // get any built-in functions
                        if ((match & SymbolMatch.Function) != 0 && (include & IncludeFunctionKind.BuiltInFunctions) != 0)
                        {
                            GetFunctionsInScope(match, null, IncludeFunctionKind.BuiltInFunctions, list);
                        }

                        // metadata symbols (tables, etc)
                        if (_currentDatabase != null)
                        {
                            var dbMatch = match;

                            if ((include & IncludeFunctionKind.DatabaseFunctions) == 0)
                                dbMatch &= ~SymbolMatch.Function;

                            _currentDatabase.GetMembers(dbMatch, list);
                        }

                        if ((match & SymbolMatch.Database) != 0)
                        {
                            _currentCluster.GetMembers(match, list);
                        }

                        if ((match & SymbolMatch.Cluster) != 0)
                        {
                            list.AddRange(_globals.Clusters);
                        }
                        break;

                    // aggregate scopes only see aggregate functions
                    case ScopeKind.Aggregate:
                        if ((match & SymbolMatch.Function) != 0)
                        {
                            GetFunctionsInScope(match, null, include, list);
                        }
                        break;

                    // plug-in scopes only see plug-in functions
                    case ScopeKind.PlugIn:
                        if ((match & SymbolMatch.Function) != 0)
                        {
                            GetFunctionsInScope(match, null, include, list);
                        }
                        break;
                }
            }
        }

        private void GetSpecialFunctions(string name, List<Symbol> functions)
        {
            if (_pathScope != null)
            {
                // these special methods show up as dottable methods on their respective types
                switch (_pathScope.Kind)
                {
                    case SymbolKind.Cluster:
                        if (name == null || Functions.Database.Name == name)
                            functions.Add(Functions.Database);
                        break;
                    case SymbolKind.Database:
                        if (name == null || Functions.Database.Name == name)
                            functions.Add(Functions.Table);
                        break;
                }
            }
        }

        private void GetFunctionsInScope(
            SymbolMatch match,
            string name,
            IncludeFunctionKind include,
            List<Symbol> functions,
            List<Diagnostic> diagnostics = null,
            SyntaxElement location = null)
        {
            var allFunctions = s_symbolListPool.AllocateFromPool();
            try
            {
                GetFunctionsInScope(
                    _scopeKind,
                    name,
                    include,
                    allFunctions,
                    diagnostics,
                    location);

                foreach (var fn in allFunctions)
                {
                    if (fn.Matches(match))
                    {
                        functions.Add(fn);
                    }
                }
            }
            finally
            {
                s_symbolListPool.ReturnToPool(allFunctions);
            }
        }

        private void GetFunctionsInScope(
            ScopeKind kind,
            string name,
            IncludeFunctionKind include,
            List<Symbol> functions,
            List<Diagnostic> diagnostics = null, 
            SyntaxElement location = null)
        {
            if (_pathScope != null)
            {
                GetSpecialFunctions(name, functions);
            }
            else
            {
                switch (kind)
                {
                    case ScopeKind.Aggregate:
                        if (name == null)
                        {
                            functions.AddRange(_globals.Aggregates);
                            GetFunctionsInScope(ScopeKind.Normal, name, include, functions);
                        }
                        else
                        {
                            var fn = _globals.GetAggregate(name);
                            if (fn != null)
                            {
                                functions.Add(fn);
                            }
                            else
                            {
                                GetFunctionsInScope(ScopeKind.Normal, name, include, functions);
                            }

                            if (functions.Count == 0 && diagnostics != null)
                            {
                                diagnostics.Add(DiagnosticFacts.GetAggregateFunctionNotDefined(name).WithLocation(location));
                            }
                        }
                        break;

                    case ScopeKind.PlugIn:
                        if(name == null)
                        {
                            functions.AddRange(_globals.PlugIns);
                        }
                        else
                        {
                            var fn = _globals.GetPlugIn(name);
                            if (fn != null)
                            {
                                functions.Add(fn);
                            }
                            else if (diagnostics != null)
                            {
                                diagnostics.Add(DiagnosticFacts.GetPlugInFunctionNotDefined(name).WithLocation(location));
                            }
                        }
                        break;

                    default:
                        if ((include & IncludeFunctionKind.BuiltInFunctions) != 0)
                        {
                            if (name == null)
                            {
                                functions.AddRange(_globals.Functions);
                            }
                            else if (functions.Count == 0)
                            {
                                var fn = _globals.GetFunction(name);
                                if (fn != null)
                                {
                                    functions.Add(fn);
                                }
                            }
                        }

                        if ((name == null || functions.Count == 0) && (include & IncludeFunctionKind.LocalFunctions) != 0)
                        {
                            GetDeclaredFunctionsInScope(name, functions);
                        }

                        if ((name == null || functions.Count == 0) && (include & IncludeFunctionKind.DatabaseFunctions) != 0 && _currentDatabase != null)
                        {
                            var oldCount = functions.Count;
                            _currentDatabase.GetMembers(name, SymbolMatch.Function, functions);

                            if (functions.Count == oldCount && diagnostics != null)
                            {
                                diagnostics.Add(DiagnosticFacts.GetScalarFunctionNotDefined(name).WithLocation(location));
                            }
                        }
                        break;
                }
            }
        }

        private void GetDeclaredFunctionsInScope(string name, List<Symbol> functions)
        {
            var locals = s_symbolListPool.AllocateFromPool();
            try
            {
                _localScope.GetSymbols(name, SymbolMatch.Local, locals);

                foreach (Symbol local in locals)
                {
                    if (GetResultType(local) is FunctionSymbol fn)
                    {
                        functions.Add(fn);
                    }
                }
            }
            finally
            {
                s_symbolListPool.ReturnToPool(locals);
            }
        }
#endregion

        #region Common definitions
        private static ObjectPool<List<Symbol>> s_symbolListPool =
            new ObjectPool<List<Symbol>>(() => new List<Symbol>(), list => list.Clear());

        private static ObjectPool<List<Diagnostic>> s_diagnosticListPool =
            new ObjectPool<List<Diagnostic>>(() => new List<Diagnostic>(), list => list.Clear());

        private static ObjectPool<List<ColumnSymbol>> s_columnListPool =
            new ObjectPool<List<ColumnSymbol>>(() => new List<ColumnSymbol>(), list => list.Clear());

        private static ObjectPool<List<TableSymbol>> s_tableListPool =
            new ObjectPool<List<TableSymbol>>(() => new List<TableSymbol>(), list => list.Clear());

        private static ObjectPool<List<FunctionSymbol>> s_functionListPool =
            new ObjectPool<List<FunctionSymbol>>(() => new List<FunctionSymbol>(), list => list.Clear());

        private static ObjectPool<List<Signature>> s_signatureListPool =
            new ObjectPool<List<Signature>>(() => new List<Signature>(), list => list.Clear());

        private static ObjectPool<List<PatternSignature>> s_patternListPool =
            new ObjectPool<List<PatternSignature>>(() => new List<PatternSignature>(), list => list.Clear());

        private static ObjectPool<List<Expression>> s_expressionListPool =
            new ObjectPool<List<Expression>>(() => new List<Expression>(), list => list.Clear());

        private static ObjectPool<List<TypeSymbol>> s_typeListPool =
            new ObjectPool<List<TypeSymbol>>(() => new List<TypeSymbol>(), list => list.Clear());

        private static ObjectPool<HashSet<string>> s_stringSetPool =
            new ObjectPool<HashSet<string>>(() => new HashSet<string>(), s => s.Clear());

        private static ObjectPool<UniqueNameTable> s_uniqueNameTablePool =
            new ObjectPool<UniqueNameTable>(() => new UniqueNameTable(), t => t.Clear());

        private static ObjectPool<ProjectionBuilder> s_projectionBuilderPool =
            new ObjectPool<ProjectionBuilder>(() => new ProjectionBuilder(), b => b.Clear());

        private static readonly SemanticInfo LiteralBoolInfo = new SemanticInfo(ScalarTypes.Bool, isConstant: true);
        private static readonly SemanticInfo LiteralIntInfo = new SemanticInfo(ScalarTypes.Int, isConstant: true);
        private static readonly SemanticInfo LiteralLongInfo = new SemanticInfo(ScalarTypes.Long, isConstant: true);
        private static readonly SemanticInfo LiteralRealInfo = new SemanticInfo(ScalarTypes.Real, isConstant: true);
        private static readonly SemanticInfo LiteralDecimalInfo = new SemanticInfo(ScalarTypes.Decimal, isConstant: true);
        private static readonly SemanticInfo LiteralStringInfo = new SemanticInfo(ScalarTypes.String, isConstant: true);
        private static readonly SemanticInfo LiteralDateTimeInfo = new SemanticInfo(ScalarTypes.DateTime, isConstant: true);
        private static readonly SemanticInfo LiteralTimeSpanInfo = new SemanticInfo(ScalarTypes.TimeSpan, isConstant: true);
        private static readonly SemanticInfo LiteralGuidInfo = new SemanticInfo(ScalarTypes.Guid, isConstant: true);
        private static readonly SemanticInfo LiteralTypeInfo = new SemanticInfo(ScalarTypes.Type, isConstant: true);
        private static readonly SemanticInfo LiteralDynamicInfo = new SemanticInfo(ScalarTypes.Dynamic, isConstant: true);
        private static readonly SemanticInfo ErrorInfo = new SemanticInfo(ErrorSymbol.Instance);
        private static readonly SemanticInfo VoidInfo = new SemanticInfo(VoidSymbol.Instance);
        #endregion

        #region Name binding
        private static bool IsFunctionCallName(SyntaxNode name)
        {
            return name.Parent is FunctionCallExpression fn && fn.Name == name;
        }

        private static bool IsInvocableFunctionName(SyntaxNode name)
        {
            return name.GetFirstAncestor<CustomCommand>() == null;
        }

        private static bool IsPossibleInvocableFunctionWithoutArgumentList(SyntaxNode name)
        {
            return !IsFunctionCallName(name) && IsInvocableFunctionName(name);
        }

        private static bool IsInCommand(SyntaxNode location)
        {
            var command = location.GetFirstAncestor<Command>();
            var functionBody = location.GetFirstAncestor<FunctionBody>();
            return command != null && functionBody == null;
        }

        private SemanticInfo BindName(string name, SymbolMatch match, Expression location)
        {
            if (name == "")
                return ErrorInfo;

            if (_pathScope != null)
            {
                if (_pathScope == ScalarTypes.Dynamic)
                {
                    return LiteralDynamicInfo;
                }
                else if (_pathScope == ErrorSymbol.Instance)
                {
                    return ErrorInfo;
                }
            }
            else if (name == "$left" && _rowScope != null && _rightRowScope != null)
            {
                var tuple = GetTuple(_rowScope);
                return new SemanticInfo(tuple, tuple);
            }
            else if (name == "$right" && _rightRowScope != null)
            {
                var tuple = GetTuple(_rightRowScope);
                return new SemanticInfo(tuple, tuple);
            }

            var list = s_symbolListPool.AllocateFromPool();
            try
            {
                bool allowZeroArgumentInvocation = false;

                if (_pathScope != null)
                {
                    if (!(_pathScope is TableSymbol) || IsInCommand(location))
                    {
                        _pathScope.GetMembers(name, match, list);

                        if (list.Count == 0)
                        {
                            if (_pathScope is DatabaseSymbol ds)
                            {
                                if (name == Functions.Table.Name)
                                {
                                    list.Add(Functions.Table);
                                }
                                else if (ds.IsOpen)
                                {
                                    var table = GetOpenTable(name, ds);
                                    return new SemanticInfo(table, table);
                                }
                            }
                            else if (_pathScope is ClusterSymbol cs && name == Functions.Database.Name)
                            {
                                list.Add(Functions.Database);
                            }
                        }
                        else
                        {
                            // database functions do not require argument list if it has zero arguments.
                            allowZeroArgumentInvocation = true;
                        }
                    }
                }
                else
                {
                    // first check binding against any columns in the row scope
                    if (_rowScope != null)
                    {
                        _rowScope.GetMembers(name, match, list);
                    }

                    // try secondary right-side row scope (from join operator)
                    if (list.Count == 0 && _rightRowScope != null)
                    {
                        _rightRowScope.GetMembers(name, match, list);
                    }

                    // try local variables (includes any user-defined functions)
                    if (list.Count == 0)
                    {
                        _localScope.GetSymbols(name, match, list);

                        // user defined functions do not require argument list if it has not arguments
                        allowZeroArgumentInvocation = list.Count > 0;
                    }

                    // look for zero-argument functions
                    if (list.Count == 0 && IsPossibleInvocableFunctionWithoutArgumentList(location) && (match & SymbolMatch.Function) != 0)
                    {
                        // database functions only (locally defined functions are already handled above)
                        GetFunctionsInScope(_scopeKind, name, IncludeFunctionKind.DatabaseFunctions, list);

                        // remove any function that cannot be called with zero arguments
                        for (int i = list.Count - 1; i >= 0; i--)
                        {
                            var fn = list[i] as FunctionSymbol;
                            if (fn == null || fn.MinArgumentCount > 0)
                            {
                                list.RemoveAt(i);
                            }
                        }

                        // database functions do not require argument list if it has zero arguments.
                        allowZeroArgumentInvocation = list.Count > 0;
                    }

                    // other items in database (tables, etc)
                    if (list.Count == 0 && _currentDatabase != null)
                    {
                        _currentDatabase.GetMembers(name, match, list);
                    }

                    // databases can be directly referenced in commands
                    if (list.Count == 0 && _currentCluster != null && (match & SymbolMatch.Database) != 0)
                    {
                        _currentCluster.GetMembers(name, match, list);
                    }

                    // look for any built-in functions with matching name (even with those with parameters)
                    if (list.Count == 0 && (match & SymbolMatch.Function) != 0)
                    {
                        GetFunctionsInScope(_scopeKind, name, IncludeFunctionKind.BuiltInFunctions, list);
                    }

                    // infer column for this otherwise unbound reference?
                    if (list.Count == 0 && _rowScope != null && _rowScope.IsOpen && (match & SymbolMatch.Column) != 0)
                    {
                        // table is open, so create a dynamic column for the otherwise unbound name
                        list.Add(GetOpenColumn(name, ScalarTypes.Dynamic, _rowScope));
                    }
                }

                if (list.Count == 1)
                {
                    var item = list[0];
                    var resultType = GetResultType(item);

                    // check for zero-parameter function invocation not part of a function call node
                    if (resultType is FunctionSymbol fn && IsPossibleInvocableFunctionWithoutArgumentList(location))
                    {
                        var sig = fn.Signatures.FirstOrDefault(s => s.MinArgumentCount == 0);
                        if (sig != null && allowZeroArgumentInvocation)
                        {
                            var sigResult = GetSignatureResult(sig, EmptyReadOnlyList<Expression>.Instance, EmptyReadOnlyList<TypeSymbol>.Instance);
                            return new SemanticInfo(item, sigResult.Type, expander: sigResult.Expander);
                        }
                        else
                        {
                            var returnType = GetCommonReturnType(fn.Signatures, EmptyReadOnlyList<Expression>.Instance, EmptyReadOnlyList<TypeSymbol>.Instance);
                            return new SemanticInfo(item, returnType, DiagnosticFacts.GetFunctionRequiresArgumentList(name).WithLocation(location));
                        }
                    }
                    else
                    {
                        return CreateSemanticInfo(item);
                    }
                }
                else if (list.Count == 0)
                {
                    if (IsFunctionCallName(location))
                    {
                        if (_globals.GetAggregate(name) != null
                            && _scopeKind != ScopeKind.Aggregate)
                        {
                            return new SemanticInfo(ErrorSymbol.Instance, DiagnosticFacts.GetAggregateNotAllowedInThisContext(name).WithLocation(location));
                        }
                        else
                        {
                            return new SemanticInfo(ErrorSymbol.Instance, DiagnosticFacts.GetNameDoesNotReferToAnyKnownFunction(name).WithLocation(location));
                        }
                    }

                    return new SemanticInfo(ErrorSymbol.Instance, DiagnosticFacts.GetNameDoesNotReferToAnyKnownItem(name).WithLocation(location));
                }
                else
                {
                    return new SemanticInfo(new GroupSymbol(list.ToList()), ErrorSymbol.Instance, DiagnosticFacts.GetNameRefersToMoreThanOneItem(name).WithLocation(location));
                }
            }
            finally
            {
                s_symbolListPool.ReturnToPool(list);
            }
        }

        private static void GetWildcardSymbols(string pattern, IReadOnlyList<Symbol> symbols, List<Symbol> matchingSymbols)
        {
            foreach (var symbol in symbols)
            {
                if (KustoFacts.Matches(pattern, symbol.Name))
                {
                    matchingSymbols.Add(symbol);
                }
            }
        }
#endregion

        #region Operator binding
        private SemanticInfo GetBinaryOperatorInfo(OperatorKind kind, Expression left, Expression right, SyntaxElement location)
        {
            return GetBinaryOperatorInfo(kind, left, GetResultTypeOrError(left), right, GetResultTypeOrError(right), location);
        }

        private SemanticInfo GetBinaryOperatorInfo(OperatorKind kind, Expression left, TypeSymbol leftType, Expression right, TypeSymbol rightType, SyntaxElement location)
        {
            var arguments = s_expressionListPool.AllocateFromPool();
            var argumentTypes = s_typeListPool.AllocateFromPool();

            try
            {
                arguments.Add(left);
                arguments.Add(right);

                argumentTypes.Add(leftType);
                argumentTypes.Add(rightType);

                return GetOperatorInfo(kind, arguments, argumentTypes, location);
            }
            finally
            {
                s_expressionListPool.ReturnToPool(arguments);
                s_typeListPool.ReturnToPool(argumentTypes);
            }
        }

        private SemanticInfo GetUnaryOperatorInfo(OperatorKind kind, Expression operand, SyntaxElement location)
        {
            var arguments = s_expressionListPool.AllocateFromPool();

            try
            {
                arguments.Add(operand);

                return GetOperatorInfo(kind, arguments, location);
            }
            finally
            {
                s_expressionListPool.ReturnToPool(arguments);
            }
        }

        private SemanticInfo GetOperatorInfo(OperatorKind kind, IReadOnlyList<Expression> arguments, SyntaxElement location)
        {
            var argumentTypes = s_typeListPool.AllocateFromPool();

            try
            {
                for (int i = 0; i < arguments.Count; i++)
                {
                    argumentTypes.Add(GetResultTypeOrError(arguments[i]));
                }

                return GetOperatorInfo(kind, arguments, argumentTypes, location);
            }
            finally
            {
                s_typeListPool.ReturnToPool(argumentTypes);
            }
        }

        private SemanticInfo GetOperatorInfo(OperatorKind kind, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes, SyntaxElement location)
        {
            var matchingSignatures = s_signatureListPool.AllocateFromPool();
            var diagnostics = s_diagnosticListPool.AllocateFromPool();

            try
            {
                var op = _globals.GetOperator(kind);

                GetBestMatchingSignatures(op.Signatures, arguments, argumentTypes, matchingSignatures);

                if (matchingSignatures.Count == 1)
                {
                    CheckSignature(matchingSignatures[0], arguments, argumentTypes, location, diagnostics);
                    var sigResult = GetSignatureResult(matchingSignatures[0], arguments, argumentTypes);
                    return new SemanticInfo(matchingSignatures[0].Symbol, sigResult.Type, diagnostics, isConstant: AllAreConstant(arguments));
                }
                else
                {
                    if (!ArgumentsHaveErrors(argumentTypes))
                    {
                        diagnostics.Add(DiagnosticFacts.GetOperatorNotDefined(location.ToString(IncludeTrivia.Interior), argumentTypes).WithLocation(location));
                    }

                    var returnType = GetCommonReturnType(matchingSignatures, arguments, argumentTypes);
                    return new SemanticInfo(matchingSignatures[0].Symbol, returnType, diagnostics);
                }
            }
            finally
            {
                s_signatureListPool.ReturnToPool(matchingSignatures);
                s_diagnosticListPool.ReturnToPool(diagnostics);
            }
        }

        private bool AllAreConstant(IReadOnlyList<Expression> expressions)
        {
            for(int i = 0; i < expressions.Count; i++)
            {
                if (!GetIsConstant(expressions[i]))
                    return false;
            }

            return true;
        }

        private static OperatorKind GetOperatorKind(SyntaxKind kind)
        {
            switch (kind)
            {
                case SyntaxKind.AddExpression:
                    return OperatorKind.Add;
                case SyntaxKind.SubtractExpression:
                    return OperatorKind.Subtract;
                case SyntaxKind.MultiplyExpression:
                    return OperatorKind.Multiply;
                case SyntaxKind.DivideExpression:
                    return OperatorKind.Divide;
                case SyntaxKind.ModuloExpression:
                    return OperatorKind.Modulo;
                case SyntaxKind.UnaryMinusExpression:
                    return OperatorKind.UnaryMinus;
                case SyntaxKind.UnaryPlusExpression:
                    return OperatorKind.UnaryPlus;
                case SyntaxKind.EqualExpression:
                    return OperatorKind.Equal;
                case SyntaxKind.NotEqualExpression:
                    return OperatorKind.NotEqual;
                case SyntaxKind.LessThanExpression:
                    return OperatorKind.LessThan;
                case SyntaxKind.LessThanOrEqualExpression:
                    return OperatorKind.LessThanOrEqual;
                case SyntaxKind.GreaterThanExpression:
                    return OperatorKind.GreaterThan;
                case SyntaxKind.GreaterThanOrEqualExpression:
                    return OperatorKind.GreaterThanOrEqual;
                case SyntaxKind.EqualTildeExpression:
                    return OperatorKind.EqualTilde;
                case SyntaxKind.BangTildeExpression:
                    return OperatorKind.BangTilde;
                case SyntaxKind.HasExpression:
                    return OperatorKind.Has;
                case SyntaxKind.HasCsExpression:
                    return OperatorKind.HasCs;
                case SyntaxKind.NotHasExpression:
                    return OperatorKind.NotHas;
                case SyntaxKind.NotHasCsExpression:
                    return OperatorKind.NotHasCs;
                case SyntaxKind.HasPrefixExpression:
                    return OperatorKind.HasPrefix;
                case SyntaxKind.HasPrefixCsExpression:
                    return OperatorKind.HasPrefixCs;
                case SyntaxKind.NotHasPrefixExpression:
                    return OperatorKind.NotHasPrefix;
                case SyntaxKind.NotHasPrefixCsExpression:
                    return OperatorKind.NotHasPrefixCs;
                case SyntaxKind.HasSuffixExpression:
                    return OperatorKind.HasSuffix;
                case SyntaxKind.HasSuffixCsExpression:
                    return OperatorKind.HasSuffixCs;
                case SyntaxKind.NotHasSuffixExpression:
                    return OperatorKind.NotHasSuffix;
                case SyntaxKind.NotHasSuffixCsExpression:
                    return OperatorKind.NotHasSuffixCs;
                case SyntaxKind.LikeExpression:
                    return OperatorKind.Like;
                case SyntaxKind.LikeCsExpression:
                    return OperatorKind.LikeCs;
                case SyntaxKind.NotLikeExpression:
                    return OperatorKind.NotLike;
                case SyntaxKind.NotLikeCsExpression:
                    return OperatorKind.NotLikeCs;
                case SyntaxKind.ContainsExpression:
                    return OperatorKind.Contains;
                case SyntaxKind.ContainsCsExpression:
                    return OperatorKind.ContainsCs;
                case SyntaxKind.NotContainsExpression:
                    return OperatorKind.NotContains;
                case SyntaxKind.NotContainsCsExpression:
                    return OperatorKind.NotContainsCs;
                case SyntaxKind.StartsWithExpression:
                    return OperatorKind.StartsWith;
                case SyntaxKind.StartsWithCsExpression:
                    return OperatorKind.StartsWithCs;
                case SyntaxKind.NotStartsWithExpression:
                    return OperatorKind.NotStartsWith;
                case SyntaxKind.NotStartsWithCsExpression:
                    return OperatorKind.NotStartsWithCs;
                case SyntaxKind.EndsWithExpression:
                    return OperatorKind.EndsWith;
                case SyntaxKind.EndsWithCsExpression:
                    return OperatorKind.EndsWithCs;
                case SyntaxKind.NotEndsWithExpression:
                    return OperatorKind.NotEndsWith;
                case SyntaxKind.NotEndsWithCsExpression:
                    return OperatorKind.NotEndsWith;
                case SyntaxKind.MatchesRegexExpression:
                    return OperatorKind.MatchRegex;
                case SyntaxKind.InExpression:
                    return OperatorKind.In;
                case SyntaxKind.InCsExpression:
                    return OperatorKind.InCs;
                case SyntaxKind.NotInExpression:
                    return OperatorKind.NotIn;
                case SyntaxKind.NotInCsExpression:
                    return OperatorKind.NotInCs;
                case SyntaxKind.BetweenExpression:
                    return OperatorKind.Between;
                case SyntaxKind.NotBetweenExpression:
                    return OperatorKind.NotBetween;
                case SyntaxKind.AndExpression:
                    return OperatorKind.And;
                case SyntaxKind.OrExpression:
                    return OperatorKind.Or;
                case SyntaxKind.SearchExpression:
                    return OperatorKind.Search;
                case SyntaxKind.HasAnyExpression:
                    return OperatorKind.HasAny;
                default:
                    return OperatorKind.None;
            }
        }
#endregion

        #region Signature binding
        private void GetArgumentsAndTypes(
            FunctionCallExpression functionCall,
            List<Expression> arguments,
            List<TypeSymbol> argumentTypes)
        {
            var expressions = functionCall.ArgumentList.Expressions;

            for (int i = 0, n = expressions.Count; i < n; i++)
            {
                var arg = expressions[i].Element;
                arguments.Add(arg);
                argumentTypes.Add(GetResultTypeOrError(arg));
            }

            if (IsInvokeOperatorFunctionCall(functionCall))
            {
                // add fake argument to represent the implicit value
                arguments.Insert(0, functionCall.Name);
                argumentTypes.Insert(0, _implicitArgumentType);
            }
        }

        private struct SignatureResult
        {
            public TypeSymbol Type { get; }

            public Func<SyntaxNode> Expander { get; }

            public SignatureResult(TypeSymbol type, Func<SyntaxNode> expander)
            {
                this.Type = type;
                this.Expander = expander;
            }

            public static implicit operator SignatureResult(TypeSymbol type)
            {
                return new SignatureResult(type, null);
            }
        }

        /// <summary>
        /// Gets the return type of the signature when invoked with the specified arguments.
        /// </summary>
        private SignatureResult GetSignatureResult(
            Signature signature,
            IReadOnlyList<Expression> arguments,
            IReadOnlyList<TypeSymbol> argumentTypes)
        {
            switch (signature.ReturnKind)
            {
                case ReturnTypeKind.Declared:
                    return signature.DeclaredReturnType;

                case ReturnTypeKind.Computed:
                    return this.GetComputedSignatureResult(signature, arguments, argumentTypes);

                case ReturnTypeKind.Parameter0:
                    var iArg = signature.GetArgumentIndex(signature.Parameters[0], arguments);
                    return iArg >= 0 ? argumentTypes[iArg] : ErrorSymbol.Instance;

                case ReturnTypeKind.Parameter1:
                    iArg = signature.GetArgumentIndex(signature.Parameters[1], arguments);
                    return iArg >= 0 ? argumentTypes[iArg] : ErrorSymbol.Instance;

                case ReturnTypeKind.Parameter2:
                    iArg = signature.GetArgumentIndex(signature.Parameters[2], arguments);
                    return iArg >= 0 ? argumentTypes[iArg] : ErrorSymbol.Instance;

                case ReturnTypeKind.ParameterN:
                    iArg = signature.GetArgumentIndex(signature.Parameters[signature.Parameters.Count - 1], arguments);
                    return iArg >= 0 ? argumentTypes[iArg] : ErrorSymbol.Instance;

                case ReturnTypeKind.ParameterNLiteral:
                    iArg = signature.GetArgumentIndex(signature.Parameters[signature.Parameters.Count - 1], arguments);
                    return iArg >= 0 ? GetTypeOfType(arguments[iArg]) : ErrorSymbol.Instance;

                case ReturnTypeKind.Parameter0Promoted:
                    iArg = signature.GetArgumentIndex(signature.Parameters[0], arguments);
                    return iArg >= 0 ? Promote(argumentTypes[iArg]) : ErrorSymbol.Instance;

                case ReturnTypeKind.Common:
                    return GetCommonArgumentType(signature, arguments, argumentTypes) ?? ErrorSymbol.Instance;

                case ReturnTypeKind.Widest:
                    return GetWidestArgumentType(signature, argumentTypes) ?? ErrorSymbol.Instance;

                case ReturnTypeKind.Parameter0Cluster:
                    iArg = signature.GetArgumentIndex(signature.Parameters[0], arguments);
                    if (iArg >= 0 && TryGetLiteralStringValue(arguments[iArg], out var clusterName))
                    {
                        return GetCluster(clusterName);
                    }
                    else
                    {
                        return new ClusterSymbol("", null, isOpen: true);
                    }

                case ReturnTypeKind.Parameter0Database:
                    iArg = signature.GetArgumentIndex(signature.Parameters[0], arguments);
                    if (iArg >= 0 && TryGetLiteralStringValue(arguments[iArg], out var databaseName))
                    {
                        return GetDatabase(databaseName);
                    }
                    else
                    {
                        return new DatabaseSymbol("", null, isOpen: true);
                    }

                case ReturnTypeKind.Parameter0Table:
                    iArg = signature.GetArgumentIndex(signature.Parameters[0], arguments);
                    if (iArg >= 0 && TryGetLiteralStringValue(arguments[iArg], out var tableName))
                    {
                        return GetTableFunctionResult(tableName);
                    }
                    else
                    {
                        return TableSymbol.Empty.Open();
                    }

                case ReturnTypeKind.Custom:
                    return signature.CustomReturnType(_rowScope ?? TableSymbol.Empty, arguments, signature) ?? ErrorSymbol.Instance;

                default:
                    throw new NotImplementedException();
            }
        }

        private SignatureResult GetComputedSignatureResult(Signature signature, IReadOnlyList<Expression> arguments = null, IReadOnlyList<TypeSymbol> argumentTypes = null)
        {
            var outerScope = _localScope.Copy();

            if (signature.FunctionBodyFacts == FunctionBodyFacts.None)
            {
                // body has non-variable (fixed) return type and does not contain cluster/database/table calls.
                return new SignatureResult(
                    signature.NonVariableComputedReturnType,
                    GetDeferredCallSiteExpansion(signature, arguments, argumentTypes, outerScope));
            }
            else
            {
                var expansion = this.GetCallSiteExpansion(signature, arguments, argumentTypes, outerScope);
                var returnType = expansion?.Expression?.ResultType ?? ErrorSymbol.Instance;
                return new SignatureResult(returnType, () => expansion);
            }
        }

        private Func<SyntaxNode> GetDeferredCallSiteExpansion(Signature signature, IReadOnlyList<Expression> arguments = null, IReadOnlyList<TypeSymbol> argumentTypes = null, LocalScope outerScope = null)
        {
            SyntaxNode expansion = null;
            var args = arguments.ToReadOnly(); // force copy
            var types = argumentTypes.ToReadOnly(); // force copy

            return () =>
            {
                if (expansion == null)
                {
                    // re-introduce binding lock since deferred function can be called outside the current binding lock
                    lock (this._globalBindingCache)
                    {
                        expansion = this.GetCallSiteExpansion(signature, args, types, outerScope);
                    }
                }

                return expansion;
            };
        }

        internal static bool TryGetLiteralStringValue(Expression expression, out string value)
        {
            if (TryGetLiteralValue(expression, out var objValue))
            {
                value = objValue as string;
                return value != null;
            }
            else
            {
                value = null;
                return false;
            }
        }

        /// <summary>
        /// Gets the value of the literal if the expression is a literal or refers to literal
        /// </summary>
        internal static bool TryGetLiteralValue(Expression expression, out object value)
        {
            // named parameter?
            if (expression is SimpleNamedExpression sn)
            {
                expression = sn.Expression;
            }

            if (expression.IsLiteral)
            {
                value = expression.LiteralValue;
                return value != null;
            }
            else if (expression is NameReference nr && nr.ReferencedSymbol is VariableSymbol vs && vs.IsConstant)
            {
                value = vs.ConstantValue;
                return true;
            }
            else
            {
                value = null;
                return false;
            }
        }

        /// <summary>
        /// Gets the database addressable in the current context.
        /// </summary>
        private TypeSymbol GetDatabase(string name)
        {
            var cluster = _pathScope as ClusterSymbol ?? _currentCluster;
            return GetDatabase(name, cluster) ?? (TypeSymbol)ErrorSymbol.Instance;
        }

        /// <summary>
        /// Gets the result of calling the table() function in the current context.
        /// </summary>
        private TypeSymbol GetTableFunctionResult(string name)
        {
            var pathDb = _pathScope as DatabaseSymbol;

            if (pathDb == null)
            {
                var match = SymbolMatch.Table | SymbolMatch.Local;

                var symbols = s_symbolListPool.AllocateFromPool();
                try
                {
                    // check scope for variables, etc
                    _localScope.GetSymbols(name, match, symbols);

                    if (symbols.Count > 0)
                    {
                        var result = GetResultType(symbols[0]);
                        return result as TableSymbol ?? (TypeSymbol)ErrorSymbol.Instance;
                    }
                    else
                    {
                        return GetTable(name, _currentDatabase)
                            ?? (TypeSymbol)ErrorSymbol.Instance;
                    }
                }
                finally
                {
                    s_symbolListPool.ReturnToPool(symbols);
                }
            }
            else 
            {
                return GetTable(name, pathDb ?? _currentDatabase)
                    ?? (TypeSymbol)ErrorSymbol.Instance;
            }
        }

        /// <summary>
        /// Determines if <see cref="P:type1"/> can be promoted to <see cref="P:type2"/>
        /// </summary>
        public static bool IsPromotable(TypeSymbol type1, TypeSymbol type2)
        {
            return type1 is ScalarSymbol type1Scalar && type2 is ScalarSymbol type2Scalar && type2Scalar.IsWiderThan(type1Scalar);
        }

        /// <summary>
        /// Promotes a type to its most general form.  int -> long, decimal -> real
        /// </summary>
        private static TypeSymbol Promote(TypeSymbol symbol)
        {
            if (symbol == ScalarTypes.Int)
            {
                return ScalarTypes.Long;
            }
            else if (symbol == ScalarTypes.Decimal)
            {
                return ScalarTypes.Real;
            }
            else
            {
                return symbol;
            }
        }

        /// <summary>
        /// Gets the widest numeric type of the argument types.
        /// The widest type is the one that can contain the values of all the other types:
        /// </summary>
        private static TypeSymbol GetWidestArgumentType(Signature signature, IReadOnlyList<TypeSymbol> argumentTypes)
        {
            ScalarSymbol widestType = null;

            for (int i = 0; i < argumentTypes.Count; i++)
            {
                var argType = argumentTypes[i];

                if (argType is ScalarSymbol s && s.IsNumeric && s != widestType)
                {
                    if (widestType == null || s.IsWiderThan(widestType))
                    {
                        widestType = s;
                    }
                }
            }

            return widestType;
        }

        /// <summary>
        /// Gets the common argument type for arguments corresponding to parameters constrained to specific <see cref="ParameterTypeKind"/>.CommonXXX values.
        /// </summary>
        private static TypeSymbol GetCommonArgumentType(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes)
        {
            TypeSymbol commonType = null;

            for (int i = 0; i < argumentTypes.Count; i++)
            {
                var parameter = signature.GetParameter(arguments[i], i, argumentTypes.Count);
                if (parameter != null)
                {
                    var argType = argumentTypes[i];

                    if ((parameter.TypeKind == ParameterTypeKind.CommonScalar && argType.IsScalar)
                        || (parameter.TypeKind == ParameterTypeKind.CommonScalarOrDynamic && argType.IsScalar)
                        || (parameter.TypeKind == ParameterTypeKind.CommonNumber && IsNumber(argType))
                        || (parameter.TypeKind == ParameterTypeKind.CommonSummable && IsSummable(argType)))
                    {
                        if (commonType == null)
                        {
                            commonType = argType;
                        }
                        else if (IsPromotable(commonType, argType))
                        {
                            // a type that can be promoted to is better
                            commonType = argType;
                        }
                        else if (SymbolsAssignable(commonType, ScalarTypes.Dynamic))
                        {
                            // non-dynamic scalars are better
                            commonType = argType;
                        }
                    }
                }
            }

            return commonType;
        }

        /// <summary>
        /// Gets the common return type across a set of signatures, or error if there is no common type.
        /// The common return type is the return type all the signatures share, or the error type if the return types differ.
        /// </summary>
        private TypeSymbol GetCommonReturnType(IReadOnlyList<Signature> signatures, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes)
        {
            if (signatures.Count == 0)
            {
                return ErrorSymbol.Instance;
            }
            else if (signatures.Count == 1)
            {
                return GetSignatureResult(signatures[0], arguments, argumentTypes).Type;
            }
            else
            {
                var firstType = GetSignatureResult(signatures[0], arguments, argumentTypes).Type;

                for (int i = 1; i < signatures.Count; i++)
                {
                    var type = GetSignatureResult(signatures[i], arguments, argumentTypes).Type;
                    if (!SymbolsAssignable(type, firstType))
                        return ErrorSymbol.Instance;
                }

                return firstType;
            }
        }

        /// <summary>
        /// Gets the common scalar type amongst a set of types.
        /// This is either the one type if they are all them same type, the most promoted of the types, or the common type of the types that are not dynamic.
        /// </summary>
        private static TypeSymbol GetCommonScalarType(params TypeSymbol[] types)
        {
            TypeSymbol commonType = null;

            for (int i = 0; i < types.Length; i++)
            {
                var type = types[i];

                if (type.IsScalar)
                {
                    // TODO: should there be a general betterness between types instead of these specific rules?
                    if (commonType == null)
                    {
                        commonType = type;
                    }
                    else if (IsPromotable(commonType, type))
                    {
                        // a type that can be promoted to is better
                        commonType = type;
                    }
                    else if (SymbolsAssignable(commonType, ScalarTypes.Dynamic))
                    {
                        // non-dynamic scalars are better
                        commonType = type;
                    }
                }
            }

            return commonType;
        }

        /// <summary>
        /// Gets the signatures that best match the specified arguments.
        /// If there is no best match, then multiple signatures will be returned.
        /// </summary>
        private void GetBestMatchingSignatures(IReadOnlyList<Signature> signatures, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes, List<Signature> result)
        {
            var argCount = argumentTypes.Count;

            if (signatures.Count == 0)
            {
                return;
            }
            else if (signatures.Count == 1)
            {
                result.Add(signatures[0]);
                return;
            }

            // determine candidates
            if (signatures.Count > 1)
            {
                var closestCount = 0;
                var maxCount = 0;

                foreach (var s in signatures)
                {
                    if (argCount >= s.MinArgumentCount && argCount <= s.MaxArgumentCount)
                    {
                        result.Add(s);
                    }
                    else if (argCount < s.MinArgumentCount && closestCount > s.MinArgumentCount)
                    {
                        closestCount = s.MinArgumentCount;
                    }

                    if (s.MaxArgumentCount > maxCount)
                    {
                        maxCount = s.MaxArgumentCount;
                    }
                }

                // if we didn't already find candidates, pick all with closest count
                if (result.Count == 0)
                {
                    if (closestCount == 0)
                    {
                        closestCount = maxCount;
                    }

                    foreach (var s in signatures)
                    {
                        if (closestCount >= s.MinArgumentCount && closestCount <= s.MaxArgumentCount)
                        {
                            result.Add(s);
                        }
                    }
                }
            }

            // reduce results to best matching functions
            if (result.Count > 1)
            {
                int mostMatchingParameterCount = 0;

                // determine the most matching parameter count
                foreach (var s in result)
                {
                    var count = GetParameterMatchCount(s, arguments, argumentTypes);
                    if (count > mostMatchingParameterCount)
                    {
                        mostMatchingParameterCount = count;
                    }
                }

                // remove all candidates that do not have the most matching parameters
                for (int i = result.Count - 1; i >= 0; i--)
                {
                    var f = result[i];
                    if (GetParameterMatchCount(f, arguments, argumentTypes) != mostMatchingParameterCount)
                    {
                        result.RemoveAt(i);
                    }
                }

                // still more than one?  Try to find best match
                if (result.Count > 1)
                {
                    var best = result[0];
                    for (int i = 1; i < result.Count; i++)
                    {
                        if (IsBetterSignatureMatch(result[i], best, arguments, argumentTypes))
                        {
                            best = result[i];
                        }
                    }

                    for (int i = 0; i < result.Count; i++)
                    {
                        if (result[i] != best && !IsBetterSignatureMatch(best, result[i], arguments, argumentTypes))
                        {
                            // non-best is now better than best second time around??? must be ambiguous
                            return;
                        }
                    }

                    // one was clearly the best
                    result.Clear();
                    result.Add(best);
                }
            }
        }

        /// <summary>
        /// Determines if <see cref="P:signature1"/> is a better match than <see cref="P:signature2"/> for the specified arguments.
        /// </summary>
        private bool IsBetterSignatureMatch(Signature signature1, Signature signature2, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes)
        {
            var argCount = argumentTypes.Count;
            var matchCount1 = GetParameterMatchCount(signature1, arguments, argumentTypes);
            var matchCount2 = GetParameterMatchCount(signature2, arguments, argumentTypes);

            // if function matches all arguments but other-function does not, function is better
            if (matchCount1 == argCount && matchCount2 < argCount)
                return true;

            // function with better parameter matches wins
            Signature better = null;
            for (int i = 0; i < argumentTypes.Count; i++)
            {
                if (IsBetterParameterMatch(signature1, signature2, arguments, argumentTypes, i))
                {
                    if (better == signature2) // function1 is better here but function2 was better before, therefore it is ambiguous
                        break;

                    better = signature1;
                }
                else if (IsBetterParameterMatch(signature2, signature1, arguments, argumentTypes, i))
                {
                    if (better == signature1) // function2 is better here but function1 was better before, therefore it is ambiguous
                    {
                        better = null;
                        break;
                    }

                    better = signature2;
                }
            }

            // if function1 is clearly better on all parameter matches, function1 is better
            if (better == signature1)
                return true;

            // ambigous on parameter-to-parameter matches
            // if function1 has more matches than function2, function1 is better
            return matchCount1 > matchCount2;
        }

        /// <summary>
        /// Determines if <see cref="P:signature1"/> is a better match than <see cref="P:signature"/> for the specified argument at the corresponding parameter position.
        /// </summary>
        private bool IsBetterParameterMatch(Signature signature1, Signature signature2, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes, int argumentIndex)
        {
            var matches1 = GetParameterMatchKind(signature1, arguments, argumentTypes, argumentIndex);
            var matches2 = GetParameterMatchKind(signature2, arguments, argumentTypes, argumentIndex);
            return matches1 > matches2;
        }

        /// <summary>
        /// Determines the number of arguments that match their corresponding signature parameter.
        /// </summary>
        private int GetParameterMatchCount(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes)
        {
            var argCount = argumentTypes.Count;
            int matches = 0;

            for (int i = 0; i < argCount; i++)
            {
                if (GetParameterMatchKind(signature, arguments, argumentTypes, i) != MatchKind.None)
                {
                    matches++;
                }
            }

            return matches;
        }

        /// <summary>
        /// The kind of match that an argument can have with its corresponding signature parameter.
        /// </summary>
        private enum MatchKind
        {
            // These are in order of which is better.. a better match is one that is more specific.

            /// <summary>
            /// There is no match between the argument and the parameter.
            /// </summary>
            None,

            /// <summary>
            /// The argument's type is not the excluded type
            /// </summary>
            NotType,

            /// <summary>
            /// The argument's type is a scalar type
            /// </summary>
            Scalar,

            /// <summary>
            /// The argument's type is a summable scalar type
            /// </summary>
            Summable,

            /// <summary>
            /// The argumet's type is a number
            /// </summary>
            Number,

            /// <summary>
            /// The argument type is compatible with the parameter type
            /// </summary>
            Compatible,

            /// <summary>
            /// The arguments type can be promoted to the parameter type
            /// </summary>
            Promoted,  // smaller set than all numbers

            /// <summary>
            /// The argument's type is tabular.
            /// </summary>
            Tabular,

            /// <summary>
            /// The argument's type is a table.
            /// </summary>
            Table,

            /// <summary>
            /// The argument's type is a database
            /// </summary>
            Database,

            /// <summary>
            /// The argument's type is a cluster
            /// </summary>
            Cluster,

            /// <summary>
            /// The argument's type is one of two possible parameter types
            /// </summary>
            OneOfTwo,  // one of two explicit types?

            /// <summary>
            /// The argument's type is an exact match for the parameter type
            /// </summary>
            Exact
        }

        /// <summary>
        /// Determines the kind of match that the argument has with its corresponding signature parameter.
        /// </summary>
        private MatchKind GetParameterMatchKind(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes, int argumentIndex)
        {
            var parameter = GetParameter(signature, arguments, argumentIndex);
            return GetParameterMatchKind(signature, arguments, argumentTypes, parameter, arguments[argumentIndex], argumentTypes[argumentIndex]);
        }

        private Parameter GetParameter(Signature signature, IReadOnlyList<Expression> arguments, int argumentIndex)
        {
            if (NamedArgumentsAllowed(signature))
            {
                return signature.GetParameter(arguments[argumentIndex], argumentIndex, arguments.Count);
            }
            else
            {
                return signature.GetParameter(argumentIndex, arguments.Count);
            }
        }

        /// <summary>
        /// Determines the kind of match that the argument has with its corresponding signature parameter.
        /// </summary>
        private MatchKind GetParameterMatchKind(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes, Parameter parameter, Expression argument, TypeSymbol resultType)
        {
            if (parameter == null)
                return MatchKind.None;

            if (parameter.DefaultValueIndicator != null
                && resultType == ScalarTypes.String
                && argument is LiteralExpression lit
                && lit.LiteralValue is string value
                && value == parameter.DefaultValueIndicator)
            {
                return MatchKind.Exact;
            }

            if (argument is StarExpression)
            {
                return (parameter.ArgumentKind == ArgumentKind.Star)
                    ? MatchKind.Exact : MatchKind.None;
            }
            else if (parameter.ArgumentKind == ArgumentKind.Star)
            {
                return MatchKind.None;
            }

            switch (parameter.TypeKind)
            {
                case ParameterTypeKind.Declared:
                    if (SymbolsAssignable(parameter.DeclaredTypes, resultType, Conversion.None))
                    {
                        if (parameter.DeclaredTypes.Count == 1)
                        {
                            return MatchKind.Exact;
                        }
                        else
                        {
                            return MatchKind.OneOfTwo;
                        }
                    }
                    else if (SymbolsAssignable(parameter.DeclaredTypes, resultType, Conversion.Promotable))
                    {
                        return MatchKind.Promoted;
                    }
                    else if (AllowLooseParameterMatching(signature)
                        && SymbolsAssignable(parameter.DeclaredTypes, resultType, Conversion.Compatible))
                    {
                        return MatchKind.Compatible;
                    }
                    break;

                case ParameterTypeKind.Scalar:
                    if (resultType.IsScalar)
                        return MatchKind.Scalar;
                    break;

                case ParameterTypeKind.Integer:
                    if (IsInteger(resultType))
                        return MatchKind.OneOfTwo;
                    break;

                case ParameterTypeKind.RealOrDecimal:
                    if (IsRealOrDecimal(resultType))
                        return MatchKind.OneOfTwo;
                    break;

                case ParameterTypeKind.StringOrDynamic:
                    if (IsStringOrDynamic(resultType))
                        return MatchKind.OneOfTwo;
                    break;

                case ParameterTypeKind.IntegerOrDynamic:
                    if (IsIntegerOrDynamic(resultType))
                        return MatchKind.OneOfTwo;
                    break;

                case ParameterTypeKind.Number:
                    if (IsNumber(resultType))
                        return MatchKind.Number;
                    break;

                case ParameterTypeKind.Summable:
                    if (IsSummable(resultType))
                        return MatchKind.Summable;
                    break;

                case ParameterTypeKind.Tabular:
                case ParameterTypeKind.SingleColumnTable:
                    if (IsTabular(resultType))
                        return MatchKind.Tabular;
                    break;

                case ParameterTypeKind.Database:
                    if (IsDatabase(resultType))
                        return MatchKind.Database;
                    break;

                case ParameterTypeKind.Cluster:
                    if (IsCluster(resultType))
                        return MatchKind.Cluster;
                    break;

                case ParameterTypeKind.NotBool:
                    if (!SymbolsAssignable(resultType, ScalarTypes.Bool))
                        return MatchKind.NotType;
                    break;

                case ParameterTypeKind.NotRealOrBool:
                    if (!SymbolsAssignable(resultType, ScalarTypes.Real)
                        && !SymbolsAssignable(resultType, ScalarTypes.Bool))
                        return MatchKind.NotType;
                    break;

                case ParameterTypeKind.NotDynamic:
                    if (!SymbolsAssignable(resultType, ScalarTypes.Dynamic))
                        return MatchKind.NotType;
                    break;

                // TODO: verify these are doing the right thing...
                case ParameterTypeKind.Parameter0:
                    var p0 = signature.GetParameter(0, arguments.Count);
                    return GetParameterMatchKind(signature, arguments, argumentTypes, p0, argument, resultType);

                case ParameterTypeKind.Parameter1:
                    var p1 = signature.GetParameter(1, arguments.Count);
                    return GetParameterMatchKind(signature, arguments, argumentTypes, p1, argument, resultType);

                case ParameterTypeKind.Parameter2:
                    var p2 = signature.GetParameter(2, arguments.Count);
                    return GetParameterMatchKind(signature, arguments, argumentTypes, p2, argument, resultType);

                case ParameterTypeKind.CommonScalar:
                case ParameterTypeKind.CommonNumber:
                case ParameterTypeKind.CommonSummable:
                case ParameterTypeKind.CommonScalarOrDynamic:
                    var commonType = GetCommonArgumentType(signature, arguments, argumentTypes);
                    if (commonType != null)
                    {
                        if (SymbolsAssignable(resultType, commonType, Conversion.None))
                        {
                            return MatchKind.Exact;
                        }
                        else if (SymbolsAssignable(resultType, commonType, Conversion.Promotable))
                        {
                            return MatchKind.Promoted;
                        }
                        else if (AllowLooseParameterMatching(signature)
                            && SymbolsAssignable(resultType, commonType, Conversion.Compatible))
                        {
                            return MatchKind.Compatible;
                        }
                        else if (parameter.TypeKind == ParameterTypeKind.CommonScalarOrDynamic && SymbolsAssignable(resultType, ScalarTypes.Dynamic))
                        {
                            return MatchKind.Exact;
                        }
                    }
                    break;
            }

            return MatchKind.None;
        }
#endregion

        #region FunctionCall and Pattern binding
        private SemanticInfo BindFunctionCallOrPattern(FunctionCallExpression functionCall)
        {
            // the result type of the name should be bound to the function/pattern
            var symbol = GetResultTypeOrError(functionCall.Name);

            if (symbol is FunctionSymbol fn)
            {
                return BindFunctionCall(functionCall, fn);
            }
            else if (symbol is PatternSymbol ps)
            {
                return BindPattern(functionCall, ps);
            }
            else if (!symbol.IsError)
            {
                return new SemanticInfo(ErrorSymbol.Instance, DiagnosticFacts.GetNameIsNotAFunction(functionCall.Name.SimpleName).WithLocation(functionCall.Name));
            }
            else
            {
                return null;
            }
        }

        private static bool IsInvokeOperatorFunctionCall(FunctionCallExpression functionCall)
        {
            return functionCall.Parent is InvokeOperator
                || (functionCall.Parent is PathExpression p && p.Selector == functionCall && p.Parent is InvokeOperator);
        }

        private SemanticInfo BindFunctionCall(FunctionCallExpression functionCall, FunctionSymbol fn)
        {
            var diagnostics = s_diagnosticListPool.AllocateFromPool();
            var arguments = s_expressionListPool.AllocateFromPool();
            var argumentTypes = s_typeListPool.AllocateFromPool();
            var matchingSignatures = s_signatureListPool.AllocateFromPool();

            try
            {
                GetArgumentsAndTypes(functionCall, arguments, argumentTypes);

                GetBestMatchingSignatures(fn.Signatures, arguments, argumentTypes, matchingSignatures);

                if (matchingSignatures.Count == 1)
                {
                    CheckSignature(matchingSignatures[0], arguments, argumentTypes, functionCall.Name, diagnostics);
                    var sigResult = GetSignatureResult(matchingSignatures[0], arguments, argumentTypes);
                    return new SemanticInfo(fn, sigResult.Type, diagnostics, isConstant: fn.IsConstantFoldable && AllAreConstant(arguments), expander: sigResult.Expander);
                }
                else
                {
                    var types = arguments.Select(e => GetResultTypeOrError(e)).ToList();

                    if (arguments.Count == 0)
                    {
                        diagnostics.Add(DiagnosticFacts.GetFunctionExpectsArgumentCountRange(fn.Name, fn.MinArgumentCount, fn.MaxArgumentCount).WithLocation(functionCall.Name));
                    }
                    else
                    {
                        diagnostics.Add(DiagnosticFacts.GetFunctionNotDefinedWithMatchingParameters(functionCall.Name.SimpleName, types).WithLocation(functionCall.Name));
                    }

                    var returnType = GetCommonReturnType(matchingSignatures, arguments, argumentTypes);

                    return new SemanticInfo(fn, returnType, diagnostics, isConstant: fn.IsConstantFoldable && AllAreConstant(arguments));
                }
            }
            finally
            {
                s_diagnosticListPool.ReturnToPool(diagnostics);
                s_expressionListPool.ReturnToPool(arguments);
                s_typeListPool.ReturnToPool(argumentTypes);
                s_signatureListPool.ReturnToPool(matchingSignatures);
            }
        }

        private SemanticInfo BindPattern(FunctionCallExpression functionCall, PatternSymbol pattern)
        {
            var diagnostics = s_diagnosticListPool.AllocateFromPool();
            var matchingPatterns = s_patternListPool.AllocateFromPool();
            var arguments = s_expressionListPool.AllocateFromPool();
            try
            {
                // check arguments
                if (pattern.Parameters.Count != functionCall.ArgumentList.Expressions.Count)
                {
                    diagnostics.Add(DiagnosticFacts.GetArgumentCountExpected(pattern.Parameters.Count));
                }

                for (int i = 0, n = pattern.Parameters.Count; i < n; i++)
                {
                    var argument = functionCall.ArgumentList.Expressions[i].Element;
                    arguments.Add(argument);

                    var type = pattern.Parameters[i].DeclaredTypes[0];
                    if (CheckIsExactType(argument, type, diagnostics))
                    {
                        CheckIsLiteral(argument, diagnostics);
                    }
                }

                if (diagnostics.Count > 0)
                {
                    return new SemanticInfo(ErrorSymbol.Instance, diagnostics);
                }

                GetMatchingPatterns(pattern.Signatures, arguments, matchingPatterns);

                if (matchingPatterns.Count == 0)
                {
                    diagnostics.Add(DiagnosticFacts.GetNoPatternMatchesArguments().WithLocation(functionCall.Name));
                    return new SemanticInfo(ErrorSymbol.Instance, diagnostics);
                }

                var result = GetReturnType(matchingPatterns);
                return new SemanticInfo(pattern, result, diagnostics);
            }
            finally
            {
                s_diagnosticListPool.ReturnToPool(diagnostics);
                s_patternListPool.ReturnToPool(matchingPatterns);
                s_expressionListPool.ReturnToPool(arguments);
            }
        }

        /// <summary>
        /// Gets the set of pattern signatures that match the arguments.
        /// </summary>
        private void GetMatchingPatterns(IReadOnlyList<PatternSignature> signatures, IReadOnlyList<Expression> arguments, List<PatternSignature> matchingSignatures)
        {
            foreach (var sig in signatures)
            {
                if (PatternMatches(sig, arguments))
                {
                    matchingSignatures.Add(sig);
                }
            }
        }

        /// <summary>
        /// Determines if the pattern signature matches the arguments.
        /// </summary>
        private bool PatternMatches(PatternSignature signature, IReadOnlyList<Expression> arguments)
        {
            if (signature.ArgumentValues.Count != arguments.Count)
                return false;

            for (int i = 0; i < arguments.Count; i++)
            {
                string matchValue = signature.ArgumentValues[i];
                string argValue = arguments[i].LiteralValue?.ToString() ?? "";
                if (matchValue != argValue)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Gets the return type of the set of pattern signatures.
        /// The return type is either the type of the signature body if there is no path,
        /// or a database symbol containing variables named for each path.
        /// </summary>
        private TypeSymbol GetReturnType(IReadOnlyList<PatternSignature> signatures)
        {
            var paths = s_symbolListPool.AllocateFromPool();
            try
            {
                foreach (var signature in signatures)
                {
                    var type = GetReturnType(signature);

                    if (signature.PathValue == null)
                        return type;

                    paths.Add(new VariableSymbol(signature.PathValue.ToString(), type));
                }

                return new DatabaseSymbol("", paths);
            }
            finally
            {
                s_symbolListPool.ReturnToPool(paths);
            }
        }

        /// <summary>
        /// Gets the return type of the pattern signature.
        /// </summary>
        private TypeSymbol GetReturnType(PatternSignature signature)
        {
            if (signature.Declaration != null)
            {
                var expr = signature.Declaration.Body.Expression;
                if (expr != null)
                {
                    return GetResultTypeOrError(expr);
                }
                else
                {
                    return VoidSymbol.Instance;
                }
            }
            else
            {
                // TODO: not implemented yet
                return ErrorSymbol.Instance;
            }
        }

        /// <summary>
        /// Gets the inline expansion of an invocation of this <see cref="Signature"/>.
        /// </summary>
        internal FunctionBody GetCallSiteExpansion(Signature signature, IReadOnlyList<Expression> arguments = null, IReadOnlyList<TypeSymbol> argumentTypes = null, LocalScope outerScope = null)
        {
            if (signature.ReturnKind != ReturnTypeKind.Computed)
                return null;

            // block cycles in computation
            if (_localBindingCache.SignaturesComputingExpansion.Contains(signature))
                return null;

            _localBindingCache.SignaturesComputingExpansion.Add(signature);
            try
            {
                var callSiteInfo = GetCallSiteInfo(signature, arguments, argumentTypes);

                if (!TryGetExpansionFromCache(callSiteInfo, out var expansion))
                {
                    try
                    {
                        var functionBodyGrammar = QueryGrammar.From(_globals).FunctionBody;
                        var body = GetFunctionBody(signature);
                        expansion = functionBodyGrammar.ParseFirst(body, alwaysProduceEOF: false);

                        if (expansion != null)
                        {
                            var isDatabaseFunction = IsDatabaseFunction(signature);
                            var currentDatabase = isDatabaseFunction ? _globals.GetDatabase((FunctionSymbol)signature.Symbol) : null;
                            var currentCluster = isDatabaseFunction ? _globals.GetCluster(currentDatabase) : null;
                            BindExpansion(expansion, this, currentCluster, currentDatabase, outerScope, callSiteInfo.Locals);
                            SetSignatureBindingInfo(signature, expansion);
                        }
                    }
                    catch (Exception)
                    {
                    }

                    AddExpansionToCache(callSiteInfo, expansion);
                }

#if false
                var dx = body.GetContainedDiagnostics();
                if (dx.Count > 0)
                {
                }
#endif

                return expansion;
            }
            finally
            {
                _localBindingCache.SignaturesComputingExpansion.Remove(signature);
            }

            // Determines if the signature is from a database function
            bool IsDatabaseFunction(Signature sig)
            {
                // user functions have existing declaration syntax
                return sig.Declaration == null;
            }

            // Tries to get the expansion from global or local cache.
            bool TryGetExpansionFromCache(CallSiteInfo callsite, out FunctionBody expansion)
            {
                return _localBindingCache.CallSiteToExpansionMap.TryGetValue(callsite, out expansion)
                    || _globalBindingCache.CallSiteToExpansionMap.TryGetValue(callsite, out expansion);
            }

            // Adds expansion to global or local cache.
            void AddExpansionToCache(CallSiteInfo callsite, FunctionBody expansion)
            {
                // if there is a call to unqualified table(t) then it may require resolving using dynamic scope, so don't cache anywhere
                if ((callsite.Signature.FunctionBodyFacts & FunctionBodyFacts.Table) != 0)
                    return;

                // only add database functions that are variable in nature to global cache
                var shouldCacheGlobally = IsDatabaseFunction(callsite.Signature)
                    && callsite.Signature.FunctionBodyFacts != FunctionBodyFacts.None;

                if (shouldCacheGlobally)
                {
                    _globalBindingCache.CallSiteToExpansionMap.Add(callsite, expansion);
                }
                else
                {
                    _localBindingCache.CallSiteToExpansionMap.Add(callsite, expansion);
                }
            }
        }

        internal void SetSignatureBindingInfo(Signature signature, FunctionBody body)
        {
            if (signature.FunctionBodyFacts == null)
            {
                signature.FunctionBodyFacts = ComputeFunctionBodyFacts(signature, body);
            }

            if (!signature.HasVariableReturnType)
            {
                var returnType = body.Expression?.ResultType ?? ErrorSymbol.Instance;
                signature.NonVariableComputedReturnType = returnType;
            }
        }

        private CallSiteInfo GetCallSiteInfo(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes)
        {
            var locals = GetArgumentsAsLocals(signature, arguments, argumentTypes);
            return new CallSiteInfo(signature, locals);
        }

        private IReadOnlyList<VariableSymbol> GetArgumentsAsLocals(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes)
        {
            var locals = new List<VariableSymbol>();

            foreach (var p in signature.Parameters)
            {
                var argIndex = arguments != null ? signature.GetArgumentIndex(p, arguments) : -1;

                if (argIndex >= 0)
                {
                    var arg = arguments[argIndex];
                    var argType = argumentTypes != null ? argumentTypes[argIndex] : arg.ResultType;
                    var isLiteral = Binding.Binder.TryGetLiteralValue(arg, out var literalValue);
                    locals.Add(new VariableSymbol(p.Name, argType, isLiteral, literalValue));
                }
                else
                {
                    var type = GetRepresentativeType(p);

                    var isConstant = p.IsOptional && p.DefaultValue != null;
                    object constantValue = null;
                    if (isConstant)
                    {
                        TryGetLiteralValue(p.DefaultValue, out constantValue);
                    }

                    locals.Add(new VariableSymbol(p.Name, type, isConstant, constantValue));
                }
            }

            return locals.ToReadOnly();
        }

        /// <summary>
        /// Builds an expanded declaration of the function customized given the arguments used at the call site.
        /// </summary>
        private string GetFunctionBody(Signature signature)
        {
            var body = signature.Body.Trim();

            if (!body.StartsWith("{"))
                body = "{" + body;

            if (!body.EndsWith("}"))
                body += "\n}";

            return body;
        }

        private static TypeSymbol GetRepresentativeType(Parameter parameter)
        {
            switch (parameter.TypeKind)
            {
                case ParameterTypeKind.Declared:
                    return parameter.DeclaredTypes[0];
                case ParameterTypeKind.Tabular:
                case ParameterTypeKind.SingleColumnTable:
                    return TableSymbol.Empty;
                default:
                    return ScalarTypes.Dynamic;
            }
        }

        private FunctionBodyFacts ComputeFunctionBodyFacts(Signature signature, FunctionBody body)
        {
            var result = FunctionBodyFacts.None;
            var isTabular = body.Expression?.ResultType is TableSymbol;

            // look for explicit calls to table(), database() or cluster() functions
            foreach (var fc in body.GetDescendants<FunctionCallExpression>(
                _fc => _fc.ReferencedSymbol == Functions.Table || _fc.ReferencedSymbol == Functions.Database || _fc.ReferencedSymbol == Functions.Cluster))
            {
                if (fc.ReferencedSymbol == Functions.Table)
                {
                    // distinguish between database(d).table(t) vs just table(t)
                    // since table(t) can see variables in dynamic scope
                    if (fc.Parent is PathExpression p && p.Selector == fc)
                    {
                        result |= FunctionBodyFacts.QualifiedTable;
                    }
                    else
                    {
                        result |= FunctionBodyFacts.Table;
                    }
                }
                else if (fc.ReferencedSymbol == Functions.Database)
                {
                    result |= FunctionBodyFacts.Database;
                }
                else if (fc.ReferencedSymbol == Functions.Cluster)
                {
                    result |= FunctionBodyFacts.Cluster;
                }

                // if the argument is not a literal, then the function likely has a variable return schema
                // note: it might not, but that would require full flow analysis of result type back to inputs.
                var isLiteral = fc.ArgumentList.Expressions.Count > 0 && fc.ArgumentList.Expressions[0].Element.IsLiteral;
                if (!isLiteral && isTabular)
                {
                    result |= FunctionBodyFacts.VariableReturn;
                }
            }

            if (isTabular && signature.Parameters.Any(p => p.IsTabular))
            {
                result |= FunctionBodyFacts.VariableReturn;
            }

            // look for any function calls that themselves that have relevant content
            foreach (var fce in body.GetDescendants<Expression>(fc => fc.ReferencedSymbol is FunctionSymbol))
            {
                var facts = GetFunctionBodyFacts(fce);
                result |= facts;
            }

            return result;
        }

        private FunctionBodyFacts GetFunctionBodyFacts(Expression expr)
        {
            if (expr.ReferencedSymbol is FunctionSymbol fs)
            {
                var signature = fs.Signatures[0];

                if (signature.FunctionBodyFacts == null)
                {
                    if (signature.ReturnKind == ReturnTypeKind.Computed)
                    {
                        if (expr is FunctionCallExpression functionCall)
                        {
                            var arguments = s_expressionListPool.AllocateFromPool();
                            var argumentTypes = s_typeListPool.AllocateFromPool();

                            try
                            {
                                GetArgumentsAndTypes(functionCall, arguments, argumentTypes);
                                GetComputedSignatureResult(signature, arguments, argumentTypes);
                            }
                            finally
                            {
                                s_expressionListPool.ReturnToPool(arguments);
                                s_typeListPool.ReturnToPool(argumentTypes);
                            }
                        }
                        else
                        {
                            GetComputedSignatureResult(signature, EmptyReadOnlyList<Expression>.Instance, EmptyReadOnlyList<TypeSymbol>.Instance);
                        }
                    }
                    else
                    {
                        signature.FunctionBodyFacts = FunctionBodyFacts.None;
                    }
                }

                return signature.FunctionBodyFacts ?? FunctionBodyFacts.None;
            }

            return FunctionBodyFacts.None;
        }
        #endregion

        #region Declarations
        private void AddLetDeclarationToScope(LocalScope scope, LetStatement statement, List<Diagnostic> diagnostics = null)
        {
            scope.AddSymbol(GetReferencedSymbol(statement.Name));
        }

        private void AddDeclarationsToLocalScope(LocalScope scope, SyntaxList<SeparatedElement<FunctionParameter>> declarations)
        {
            for (int i = 0, n = declarations.Count; i < n; i++)
            {
                var d = declarations[i].Element;
                AddDeclarationToLocalScope(scope, d.NameAndType);
            }
        }

        private void AddDeclarationsToLocalScope(LocalScope scope, SyntaxList<SeparatedElement<NameAndTypeDeclaration>> declarations)
        {
            for (int i = 0, n = declarations.Count; i < n; i++)
            {
                var d = declarations[i].Element;
                AddDeclarationToLocalScope(scope, d);
            }
        }

        private void AddDeclarationToLocalScope(LocalScope scope, NameAndTypeDeclaration declaration, List<Diagnostic> diagnostics = null)
        {
            // referenced symbol should already be bound
            var symbol = GetReferencedSymbol(declaration.Name);
            if (symbol != null)
            {
                scope.AddSymbol(symbol); 
            }
        }

        private void BindParameterDeclarations(SyntaxList<SeparatedElement<FunctionParameter>> parameters)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i].Element;
                BindParameterDeclaration(p);
            }
        }

        private void BindParameterDeclaration(FunctionParameter node)
        {
            BindParameterDeclaration(node.NameAndType);
        }

        private void BindParameterDeclarations(SyntaxList<SeparatedElement<NameAndTypeDeclaration>> parameters)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i].Element;
                BindParameterDeclaration(p);
            }
        }

        private void BindParameterDeclaration(NameAndTypeDeclaration node)
        {
            var name = node.Name.SimpleName;
            var type = GetTypeFromTypeExpression(node.Type);

            if (!string.IsNullOrEmpty(name))
            {
                var symbol = new ParameterSymbol(name, type);
                SetSemanticInfo(node.Name, new SemanticInfo(symbol, type));
            }
        }

#endregion

        #region Tables and Columns
        /// <summary>
        /// Adds all the columns declared by the symbol to the list of columns.
        /// </summary>
        private void AddTableColumns(Symbol symbol, List<ColumnSymbol> columns)
        {
            switch (symbol)
            {
                case TableSymbol t:
                    GetDeclaredAndInferredColumns(t, columns);
                    break;
                case GroupSymbol g:
                    foreach (var s in g.Members)
                    {
                        AddTableColumns(s, columns);
                    }
                    break;
            }
        }

        private void AddTables(Symbol symbol, List<TableSymbol> tables)
        {
            switch (symbol)
            {
                case TableSymbol t:
                    tables.Add(t);
                    break;
                case GroupSymbol g:
                    foreach (var m in g.Members)
                    {
                        AddTables(m, tables);
                    }
                    break;
            }
        }

        private TableSymbol GetFindColumnsTable(FindOperator node)
        {
            var tables = GetFindTables(node);
            return GetTableOfColumnsUnifiedByName(tables);
        }

        /// <summary>
        /// Gets the set of columns from the tables applicable to the find operator.
        /// </summary>
        private void GetFindColumns(FindOperator node, List<ColumnSymbol> columns)
        {
            var tables = GetFindTables(node);
            var unifiedColumnsTable = GetTableOfColumnsUnifiedByName(tables);
            columns.AddRange(unifiedColumnsTable.Columns);
        }

        /// <summary>
        /// Get the set of tables applicable to the find operator.
        /// </summary>
        private IReadOnlyList<TableSymbol> GetFindTables(FindOperator node)
        {
            if (node.InClause != null)
            {
                return node.InClause.Expressions.Select(e => GetResultTypeOrError(e.Element)).OfType<TableSymbol>().ToReadOnly();
            }
            else
            {
                return _currentDatabase.Tables;
            }
        }

        /// <summary>
        /// Gets the set of columns from the tables applicable to the search operator.
        /// </summary>
        private TableSymbol GetSearchColumnsTable(SearchOperator node)
        {
            if (_rowScope != null && node.InClause == null)
            {
                return _rowScope;
            }

            var tables = s_tableListPool.AllocateFromPool();
            try
            {
                if (_rowScope != null)
                {
                    tables.Add(_rowScope);
                }

                if (node.InClause != null)
                {
                    tables.AddRange(node.InClause.Expressions.Select(e => GetResultTypeOrError(e.Element)).OfType<TableSymbol>());
                }

                if (_rowScope == null && node.InClause == null)
                {
                    tables.AddRange(_currentDatabase.Tables);
                }

                // access through cache
                return GetTableOfColumnsUnifiedByNameAndType(tables);
            }
            finally
            {
                s_tableListPool.ReturnToPool(tables);
            }
        }

        /// <summary>
        /// Converts a list of columns into a list of unique (unioned columns)
        /// Columns with the same name and type will be merged into one column.
        /// Columns with the same name but different type will be renamed to include the type name as a suffix.
        /// </summary>
        internal static void UnifyColumnsWithSameNameAndType(List<ColumnSymbol> columns)
        {
            var uniqueNames = s_uniqueNameTablePool.AllocateFromPool();
            var newColumns = s_columnListPool.AllocateFromPool();
            try
            {
                // TODO: pool this too?
                var map = BuildColumnNameMap(columns);

                // go through original column order and build out new column list
                for (int i = 0; i < columns.Count; i++)
                {
                    var col = columns[i];

                    if (map.TryGetValue(col.Name, out var sameNamedColumns))
                    {
                        if (sameNamedColumns.Count == 1)
                        {
                            // exactly one column with this name
                            newColumns.Add(GetUniqueColumn(col, uniqueNames));
                        }
                        else if (sameNamedColumns.Count > 1)
                        {
                            // more than one column, so lets make a unique column for each type used
                            foreach (var colType in sameNamedColumns)
                            {
                                var name = uniqueNames.GetOrAddName(col.Name + "_" + colType.Name);
                                newColumns.Add(new ColumnSymbol(name, colType));
                            }
                        }

                        // we've already handled this name, remove it so we don't try adding it again
                        map.Remove(col.Name);
                    }
                }

                // copy new list back to original
                columns.Clear();
                columns.AddRange(newColumns);
            }
            finally
            {
                s_uniqueNameTablePool.ReturnToPool(uniqueNames);
                s_columnListPool.ReturnToPool(newColumns);
            }
        }

        /// <summary>
        /// Converts list of columns to a list of columns with distinct names.
        /// If multiple columns have the same name, but differ in type, the resulting single columns has the type dynamic.
        /// </summary>
        /// <param name="columns"></param>
        internal static void UnifyColumnsWithSameName(List<ColumnSymbol> columns)
        {
            var newColumns = s_columnListPool.AllocateFromPool();
            try
            {
                var map = BuildColumnNameMap(columns);

                // go through original column order and build out new column list
                for (int i = 0; i < columns.Count; i++)
                {
                    var col = columns[i];

                    if (map.TryGetValue(col.Name, out var sameNamedColumns))
                    {
                        if (sameNamedColumns.Count == 1)
                        {
                            // exactly one column with this name (and type)
                            newColumns.Add(col);
                        }
                        else if (sameNamedColumns.Count > 1)
                        {
                            // multiple columns with same name, add a single one that uses a common type.
                            var types = sameNamedColumns.ToArray();
                            var commonType = GetCommonScalarType(types);

                            if (commonType == null)
                                commonType = ScalarTypes.Dynamic;

                            if (col.Type == commonType)
                            {
                                newColumns.Add(col);
                            }
                            else
                            {
                                newColumns.Add(new ColumnSymbol(col.Name, commonType));
                            }
                        }

                        // we've already handled this name, so remove it so we don't add it again
                        map.Remove(col.Name);
                    }
                }

                // copy new list back to original
                columns.Clear();
                columns.AddRange(newColumns);
            }
            finally
            {
                s_columnListPool.ReturnToPool(newColumns);
            }
        }

        /// <summary>
        /// Converts a list of columns into a list of unique columns by name.
        /// Columns with the same name will be renamed to include a numeric suffix.
        /// </summary>
        internal static void MakeColumnNamesUnique(List<ColumnSymbol> columns)
        {
            var names = s_uniqueNameTablePool.AllocateFromPool();
            var newColumns = s_columnListPool.AllocateFromPool();
            try
            {
                // go through original column order and build out new column list
                for (int i = 0; i < columns.Count; i++)
                {
                    var col = columns[i];
                    newColumns.Add(GetUniqueColumn(col, names));
                }

                // copy new list back to original
                columns.Clear();
                columns.AddRange(newColumns);
            }
            finally
            {
                s_uniqueNameTablePool.ReturnToPool(names);
                s_columnListPool.ReturnToPool(newColumns);
            }
        }

        /// <summary>
        /// Builds a map between names and columns with that name.
        /// </summary>
        private static Dictionary<string, List<TypeSymbol>> BuildColumnNameMap(List<ColumnSymbol> columns)
        {
            var map = new Dictionary<string, List<TypeSymbol>>();

            // build up a map between column names and types.
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (!map.TryGetValue(col.Name, out var sameNameColumns))
                {
                    sameNameColumns = new List<TypeSymbol>();
                    map.Add(col.Name, sameNameColumns);
                }

                if (!sameNameColumns.Contains(col.Type))
                {
                    sameNameColumns.Add(col.Type);
                }
            }

            return map;
        }

        /// <summary>
        /// Gets the columns that appear in both list of columns (by name)
        /// </summary>
        private static void GetCommonColumns(IReadOnlyList<ColumnSymbol> columnsA, IReadOnlyList<ColumnSymbol> columnsB, List<Symbol> result)
        {
            var columns = s_columnListPool.AllocateFromPool();
            try
            {
                GetCommonColumns(columnsA, columnsB, columns);

                foreach (var c in columns)
                {
                    result.Add(c);
                }
            }
            finally
            {
                s_columnListPool.ReturnToPool(columns);
            }
        }

        /// <summary>
        /// Gets the columns that appear in both list of columns (by name)
        /// </summary>
        private static void GetCommonColumns(IReadOnlyList<ColumnSymbol> columnsA, IReadOnlyList<ColumnSymbol> columnsB, List<ColumnSymbol> result)
        {
            var names = s_stringSetPool.AllocateFromPool();
            try
            {
                foreach (var c in columnsB)
                {
                    names.Add(c.Name);
                }

                foreach (var c in columnsA)
                {
                    if (names.Contains(c.Name))
                    {
                        result.Add(c);
                    }
                }
            }
            finally
            {
                s_stringSetPool.ReturnToPool(names);
            }
        }

        /// <summary>
        /// Gets the columns that appear in all tables.
        /// </summary>
        internal static void GetCommonColumns(IReadOnlyList<TableSymbol> tables, List<ColumnSymbol> common)
        {
            common.Clear();

            if (tables.Count == 1)
            {
                common.AddRange(tables[0].Columns);
            }
            else if (tables.Count == 2)
            {
                GetCommonColumns(tables[0].Columns, tables[1].Columns, common);
            }
            else if (tables.Count > 2)
            {
                var columnsA = s_columnListPool.AllocateFromPool();
                var columnsC = s_columnListPool.AllocateFromPool();
                try
                {
                    GetCommonColumns(tables[0].Columns, tables[1].Columns, columnsA);

                    for (int i = 2; i < tables.Count; i++)
                    {
                        GetCommonColumns(columnsA, tables[i].Columns, columnsC);

                        if (i < tables.Count - 1)
                        {
                            columnsA.Clear();
                            columnsA.AddRange(columnsC);
                            columnsC.Clear();
                        }
                    }

                    common.AddRange(columnsC);
                }
                finally
                {
                    s_columnListPool.ReturnToPool(columnsA);
                    s_columnListPool.ReturnToPool(columnsC);
                }
            }
        }

        /// <summary>
        /// Gets a column with a unique name (given a set of already used names).
        /// </summary>
        private static ColumnSymbol GetUniqueColumn(ColumnSymbol column, UniqueNameTable uniqueNames)
        {
            var uniqueName = uniqueNames.GetOrAddName(column.Name);
            if (uniqueName != column.Name)
            {
                return new ColumnSymbol(uniqueName, column.Type);
            }
            else
            {
                return column;
            }
        }

        /// <summary>
        /// Creates column symbols for all the columns declared in the schema.
        /// </summary>
        private static void CreateColumnsFromSchema(SchemaTypeExpression schema, List<ColumnSymbol> columns, HashSet<string> declaredNames, List<Diagnostic> diagnostics)
        {
            for (int i = 0, n = schema.Columns.Count; i < n; i++)
            {
                var expr = schema.Columns[i].Element;
                switch (expr)
                {
                    case NameAndTypeDeclaration nat:
                        CreateColumnsFromSchema(nat, columns, declaredNames, diagnostics);
                        break;

                    case StarExpression s:
                        // not sure what this means here yet.
                        break;
                }
            }
        }

        /// <summary>
        /// Creates a column symbol for the column declared by the <see cref="NameAndTypeDeclaration"/>
        /// </summary>
        private static void CreateColumnsFromSchema(NameAndTypeDeclaration declaration, List<ColumnSymbol> columns, HashSet<string> declaredNames, List<Diagnostic> diagnostics)
        {
            var name = declaration.Name.SimpleName;

            switch (declaration.Type)
            {
                case PrimitiveTypeExpression p:
                    var type = GetType(p); // diagnostics should already have been added
                    if (DeclareColumnName(declaredNames, name, diagnostics, declaration.Name))
                    {
                        columns.Add(new ColumnSymbol(name, type));
                    }
                    break;

                case SchemaTypeExpression s:
                    var subSchemaColumns = s_columnListPool.AllocateFromPool();
                    var subSchemaNames = s_stringSetPool.AllocateFromPool();
                    try
                    {
                        CreateColumnsFromSchema(s, subSchemaColumns, subSchemaNames, diagnostics);

                        if (DeclareColumnName(declaredNames, name, diagnostics, declaration.Name))
                        {
                            columns.Add(new ColumnSymbol(name, new TableSymbol(subSchemaColumns)));
                        }
                    }
                    finally
                    {
                        s_columnListPool.ReturnToPool(subSchemaColumns);
                        s_stringSetPool.ReturnToPool(subSchemaNames);
                    }
                    break;

                default:
                    diagnostics.Add(DiagnosticFacts.GetInvalidColumnDeclaration().WithLocation(declaration));
                    break;
            }
        }

        /// <summary>
        /// Gets the columns referenced by all expressions
        /// </summary>
        private void GetColumnsInColumnList(SyntaxList<SeparatedElement<Expression>> expressions, List<ColumnSymbol> columns, List<Diagnostic> diagnostics)
        {
            foreach (var elem in expressions)
            {
                GetReferencedColumn(elem.Element, columns, diagnostics);
            }
        }

        /// <summary>
        /// Gets the columns referenced by one expression.
        /// </summary>
        private void GetReferencedColumn(Expression expression, List<ColumnSymbol> columns, List<Diagnostic> diagnostics)
        {
            if (GetReferencedSymbol(expression) is ColumnSymbol c)
            {
                columns.Add(c);
            }
            else
            {
                diagnostics.Add(DiagnosticFacts.GetColumnExpected().WithLocation(expression));
            }
        }

        /// <summary>
        /// Gets all the columns referenced in the syntax tree.
        /// </summary>
        private void GetReferencedColumnsInTree(SyntaxNode node, List<ColumnSymbol> columns)
        {
            if (node is NameReference nr && GetReferencedSymbol(nr) is ColumnSymbol c)
            {
                if (!columns.Contains(c))
                {
                    columns.Add(c);
                }
            }

            // find all references in tree
            for (int i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                if (child is SyntaxNode sn)
                {
                    GetReferencedColumnsInTree(sn, columns);
                }
            }
        }

        /// <summary>
        /// Creates projection columns for all the expressions.
        /// </summary>
        private void CreateProjectionColumns(
            SyntaxList<SeparatedElement<Expression>> expressions, 
            ProjectionBuilder builder,
            List<Diagnostic> diagnostics,
            bool isRename = false,
            bool isReplace = false,
            bool isReorder = false,
            bool isExtend = false,
            bool aggregates = false,
            bool doNotRepeat = false)
        {
            foreach (var elem in expressions)
            {
                CreateProjectionColumns(
                    elem.Element,
                    builder,
                    diagnostics,
                    isRename: isRename,
                    isReplace: isReplace,
                    isReorder: isReorder,
                    isExtend: isExtend,
                    aggregates: aggregates,
                    doNotRepeat: doNotRepeat);
            }
        }

        /// <summary>
        /// Creates projection columns for the expression.
        /// </summary>
        private void CreateProjectionColumns(
            Expression expression,
            ProjectionBuilder builder,
            List<Diagnostic> diagnostics,
            bool isRename = false,
            bool isReplace = false,
            bool isReorder = false,
            bool isExtend = false,
            bool aggregates = false,
            bool doNotRepeat = false,
            TypeSymbol columnType = null)
        {
            ColumnSymbol col;
            TypeSymbol type;

            // look through ordered expressions to find column references
            if (expression is OrderedExpression oe)
            {
                expression = oe.Expression;
            }

            if (isRename)
            {
                switch (expression)
                {
                    case SimpleNamedExpression n:
                        if (GetReferencedSymbol(n.Expression) is ColumnSymbol cs)
                        {
                            col = builder.Rename(cs.Name, n.Name.SimpleName, diagnostics, n.Name);
                            if (col != null)
                            {
                                SetSemanticInfo(n.Name, CreateSemanticInfo(col));
                            }
                        }
                        else
                        {
                            diagnostics.Add(DiagnosticFacts.GetColumnExpected().WithLocation(n.Expression));
                        }
                        break;

                    default:
                        diagnostics.Add(DiagnosticFacts.GetRenameAssignmentExpected().WithLocation(expression));
                        break;
                }
            }
            else
            {
                switch (expression)
                {
                    case SimpleNamedExpression n:
                        {
                            // single name assigned from multi-value tuple just assigns the first value. equivalant to (name) = tuple
                            if (GetResultType(n.Expression) is TupleSymbol tu)
                            {
                                // first column has declared name so it uses declared name add/replace rule
                                col = new ColumnSymbol(n.Name.SimpleName, columnType ?? tu.Columns[0].Type);
                                builder.Declare(col, diagnostics, n.Name, replace: true);
                                SetSemanticInfo(n.Name, CreateSemanticInfo(col));

                                if (doNotRepeat)
                                {
                                    builder.DoNotAdd(tu.Columns[0]);
                                }

                                // all other columns are not declared, so they must be unique
                                for (int i = 1; i < tu.Members.Count; i++)
                                {
                                    if (GetReferencedSymbol(n.Expression) is FunctionSymbol fs1)
                                    {
                                        AddFunctionTupleResultColumn(fs1, tu.Columns[i], builder, doNotRepeat, aggregates);
                                    }
                                    else
                                    {
                                        builder.Add(tu.Columns[i], doNotRepeat: doNotRepeat);
                                    }
                                }
                            }
                            else if (n.Expression.ReferencedSymbol is ColumnSymbol c)
                            {
                                col = new ColumnSymbol(n.Name.SimpleName, columnType ?? c.Type);
                                builder.Declare(col, diagnostics, n.Name, replace: true);
                                SetSemanticInfo(n.Name, CreateSemanticInfo(col));

                                if (doNotRepeat)
                                {
                                    builder.DoNotAdd(c);
                                }
                            }
                            else
                            {
                                col = new ColumnSymbol(n.Name.SimpleName, columnType ?? GetResultTypeOrError(n.Expression));
                                builder.Declare(col, diagnostics, n.Name, replace: isExtend);
                                SetSemanticInfo(n.Name, CreateSemanticInfo(col));
                            }
                        }
                        break;

                    case CompoundNamedExpression c:
                        {
                            if (GetResultTypeOrError(c.Expression) is TupleSymbol tupleType)
                            {
                                for (int i = 0; i < tupleType.Columns.Count; i++)
                                {
                                    col = tupleType.Columns[i];
                                    type = columnType ?? col.Type;

                                    // if element has name declaration then use name declaration rule
                                    if (i < c.Names.Names.Count)
                                    {
                                        var nameDecl = c.Names.Names[i].Element;
                                        var name = nameDecl.SimpleName;
                                        col = new ColumnSymbol(name, type);

                                        builder.Declare(col, diagnostics, nameDecl, replace: isExtend);
                                        SetSemanticInfo(nameDecl, CreateSemanticInfo(col));

                                        if (doNotRepeat)
                                        {
                                            builder.DoNotAdd(tupleType.Columns[i]);
                                        }
                                    }
                                    else if (GetReferencedSymbol(c.Expression) is FunctionSymbol fs1)
                                    {
                                        AddFunctionTupleResultColumn(fs1, col, builder, doNotRepeat, aggregates);
                                    }
                                    else
                                    {
                                        // not-declared so make unique column
                                        builder.Add(col, doNotRepeat: doNotRepeat);
                                    }
                                }

                                // any additional names without matching tuple members gets a diagnostic
                                for (int i = tupleType.Members.Count; i < c.Names.Names.Count; i++)
                                {
                                    var nameDecl = c.Names.Names[i];
                                    diagnostics.Add(DiagnosticFacts.GetTheNameDoesNotHaveCorrespondingExpression().WithLocation(nameDecl));
                                }
                            }
                            else
                            {
                                diagnostics.Add(DiagnosticFacts.GetTheExpressionDoesNotHaveMultipleValues().WithLocation(c.Names));
                            }
                        }
                        break;

                    case FunctionCallExpression f:
                        if (GetResultType(f) is TupleSymbol ts
                            && GetReferencedSymbol(f) is FunctionSymbol fs)
                        {
                            foreach (ColumnSymbol c in ts.Members)
                            {
                                AddFunctionTupleResultColumn(fs, c, builder, doNotRepeat, aggregates);
                            }
                        }
                        else
                        {
                            var name = GetFunctionResultName(f, null, _rowScope);
                            col = new ColumnSymbol(name ?? "Column1", columnType ?? GetResultTypeOrError(f));
                            builder.Add(col, name ?? "Column");
                        }
                        break;

                    case StarExpression s:
                        foreach (ColumnSymbol c in GetDeclaredAndInferredColumns(_rowScope))
                        {
                            builder.Add(c, replace: true, doNotRepeat: doNotRepeat);
                        }
                        break;

                    default:
                        var rs = GetReferencedSymbol(expression);
                        if (rs is ColumnSymbol column)
                        {
                            // if the expression is a column reference, then consider it a declaration
                            builder.Declare(column.WithType(columnType ?? column.Type), diagnostics, expression, replace: isReplace);

                            if (doNotRepeat)
                            {
                                builder.DoNotAdd(column);
                            }
                        }
                        else if (rs is GroupSymbol group && isReorder)
                        {
                            // add any columns referenced in group
                            foreach (var m in group.Members)
                            {
                                if (m is ColumnSymbol c)
                                {
                                    builder.Add(c, doNotRepeat: true);
                                }
                            }
                        }
                        else if (GetResultType(expression) is GroupSymbol g)
                        {
                            diagnostics.Add(DiagnosticFacts.GetTheExpressionRefersToMoreThanOneColumn().WithLocation(expression));
                        }
                        else
                        {
                            type = GetResultTypeOrError(expression);
                            if (!type.IsError && !type.IsScalar)
                            {
                                diagnostics.Add(DiagnosticFacts.GetScalarTypeExpected().WithLocation(expression));
                            }
                            else
                            {
                                var name = GetExpressionResultName(expression, null);
                                col = new ColumnSymbol(name ?? "Column1", columnType ?? GetResultTypeOrError(expression));
                                builder.Add(col, name ?? "Column");
                            }
                        }
                        break;
                }
            }
        }

        private void AddFunctionTupleResultColumn(FunctionSymbol function, ColumnSymbol column, ProjectionBuilder builder, bool doNotRepeat, bool isAggregate)
        {
            if (builder.CanAdd(column))
            {
                var prefix = function.ResultNamePrefix;

                if (prefix != null)
                {
                    builder.Add(column.WithName(function.ResultNamePrefix + "_" + column.Name), doNotRepeat: doNotRepeat);
                }
                else
                {
                    builder.Add(column, doNotRepeat: doNotRepeat);
                }
            }
        }

        private static bool DeclareColumnName(HashSet<string> declaredNames, string newName, List<Diagnostic> diagnostics, SyntaxNode location)
        {
            if (declaredNames.Contains(newName))
            {
                diagnostics.Add(DiagnosticFacts.GetDuplicateColumnDeclaration(newName).WithLocation(location));
                return false;
            }
            else
            {
                declaredNames.Add(newName);
                return true;
            }
        }

        private static string GetFunctionResultName(FunctionCallExpression fc, string defaultName = "", TableSymbol row = null)
        {
            var fs = fc.ReferencedSymbol as FunctionSymbol;
            var kind = fs?.ResultNameKind ?? ResultNameKind.None;
            var prefix = fs?.ResultNamePrefix;

            if (kind == ResultNameKind.NameAndFirstArgument)
            {
                prefix = fs.Name;
                kind = ResultNameKind.PrefixAndFirstArgument;
            }
            else if (kind == ResultNameKind.NameAndOnlyArgument)
            {
                prefix = fs.Name;
                kind = ResultNameKind.PrefixAndOnlyArgument;
            }

            if (kind == ResultNameKind.PrefixAndFirstArgument)
            {
                if (fc.ArgumentList.Expressions.Count > 0)
                {
                    var name = GetExpressionResultName(fc.ArgumentList.Expressions[0].Element, defaultName);
                    if (prefix != null)
                    {
                        return prefix + "_" + name;
                    }
                    else
                    {
                        return name;
                    }
                }
                else if (prefix != null)
                {
                    return prefix + "_";
                }
                else
                {
                    return null;
                }
            }
            else if (kind == ResultNameKind.PrefixAndOnlyArgument 
                && fc.ArgumentList.Expressions.Count == 1)
            {
                var name = GetExpressionResultName(fc.ArgumentList.Expressions[0].Element, defaultName);
                if (prefix != null)
                {
                    return prefix + "_" + name;
                }
                else
                {
                    return name;
                }
            }
            else if (kind == ResultNameKind.FirstArgumentValueIfColumn
                && fc.ArgumentList.Expressions.Count > 0
                && fc.ArgumentList.Expressions[0].Element.ConstantValue is string name)
            {
                if (row != null && row.TryGetColumn(name, out _))
                {
                    return name;
                }
                else
                {
                    return defaultName;
                }
            }
            else if (kind == ResultNameKind.FirstArgument)
            {
                if (fc.ArgumentList.Expressions.Count > 0)
                {
                    return GetExpressionResultName(fc.ArgumentList.Expressions[0].Element, defaultName);
                }
                else
                {
                    return null;
                }
            }
            else if (kind == ResultNameKind.PrefixOnly && prefix != null)
            {
                return prefix;
            }
            else if (kind == ResultNameKind.OnlyArgument && fc.ArgumentList.Expressions.Count == 1)
            {
                return GetExpressionResultName(fc.ArgumentList.Expressions[0].Element, defaultName);
            }
            else
            {
                return defaultName;
            }
        }

        public static string GetExpressionResultName(Expression expr, string defaultName = "", TableSymbol row = null)
        {
            switch (expr)
            {
                case NameReference n:
                    return n.SimpleName;
                case BrackettedExpression be
                    when be.Expression.Kind == SyntaxKind.StringLiteralExpression
                        || be.Expression.Kind == SyntaxKind.CompoundStringLiteralExpression:
                    return (string)be.Expression.LiteralValue;
                case PathExpression p:
                    if (p.Expression.ResultType == ScalarTypes.Dynamic)
                    {
                        var left = GetExpressionResultName(p.Expression, null);
                        var right = GetExpressionResultName(p.Selector, null);
                        return $"{left}_{right}";
                    }
                    else
                    {
                        return GetExpressionResultName(p.Selector, defaultName);
                    }
                case ElementExpression e:
                    if (e.Expression.ResultType == ScalarTypes.Dynamic)
                    {
                        var left = GetExpressionResultName(e.Expression, null);
                        var right = GetExpressionResultName(e.Selector, null);
                        return $"{left}_{right}";
                    }
                    else
                    {
                        return GetExpressionResultName(e.Selector, defaultName);
                    }
                case OrderedExpression o:
                    return GetExpressionResultName(o.Expression, defaultName);
                case SimpleNamedExpression s:
                    return s.Name.SimpleName;
                case FunctionCallExpression f:
                    return GetFunctionResultName(f, defaultName, row);
                default:
                    return defaultName;
            }
        }
#endregion

        #region Other
        /// <summary>
        /// Gets the type referenced in the type expression.
        /// </summary>
        private TypeSymbol GetTypeFromTypeExpression(TypeExpression typeExpression, List<Diagnostic> diagnostics = null)
        {
            switch (typeExpression)
            {
                case PrimitiveTypeExpression p:
                    return GetType(p, diagnostics);

                case SchemaTypeExpression s:

                    if (s.Columns.Count == 1 && s.Columns[0].Element is StarExpression)
                    {
                        // (*) was the entire declaration.. no columns specified.
                        return TableSymbol.Empty;
                    }

                    var columns = s_columnListPool.AllocateFromPool();
                    try
                    {
                        for (int i = 0, n = s.Columns.Count; i < n; i++)
                        {
                            var expr = s.Columns[i].Element;
                            if (!expr.IsMissing)
                            {
                                switch (expr)
                                {
                                    case NameAndTypeDeclaration nat:
                                        var declaredType = GetTypeFromTypeExpression(nat.Type, diagnostics);
                                        var newColumn = new ColumnSymbol(nat.Name.SimpleName, declaredType);
                                        columns.Add(newColumn);

                                        SetSemanticInfo(nat.Name, GetSemanticInfo(newColumn));
                                        break;

                                    default:
                                        if (diagnostics != null)
                                        {
                                            diagnostics.Add(DiagnosticFacts.GetInvalidColumnDeclaration().WithLocation(expr));
                                        }
                                        break;
                                }
                            }
                        }

                        return new TableSymbol(columns);
                    }
                    finally
                    {
                        s_columnListPool.ReturnToPool(columns);
                    }

                default:
                    if (diagnostics != null)
                    {
                        diagnostics.Add(DiagnosticFacts.GetInvalidTypeExpression().WithLocation(typeExpression));
                    }

                    return ErrorSymbol.Instance;
            }
        }

        internal TypeSymbol GetTypeOfType(Expression typeofLiteral)
        {
            return GetReferencedSymbol(typeofLiteral) as TypeSymbol ?? ErrorSymbol.Instance;
        }

        internal static TypeSymbol GetType(PrimitiveTypeExpression primitiveType, List<Diagnostic> diagnostics = null)
        {
            var typeName = primitiveType.Type.Text;

            var type = ScalarTypes.GetSymbol(typeName);

            if (type != null)
                return type;

            if (diagnostics != null) // diagnostic already handled by lexer
            {
                diagnostics.Add(DiagnosticFacts.GetInvalidTypeName(typeName).WithLocation(primitiveType.Type));
            }

            return ErrorSymbol.Instance;
        }

        private static bool IsInteger(TypeSymbol type)
        {
            return type is ScalarSymbol s && s.IsInteger;
        }

        private static bool IsRealOrDecimal(TypeSymbol type)
        {
            return SymbolsAssignable(type, ScalarTypes.Real) || SymbolsAssignable(type, ScalarTypes.Decimal);
        }

        private static bool IsStringOrDynamic(TypeSymbol type)
        {
            return type == ScalarTypes.String || type == ScalarTypes.Dynamic;
        }

        private static bool IsNumber(TypeSymbol type)
        {
            return type is ScalarSymbol s && s.IsNumeric;
        }

        private static bool IsIntegerOrDynamic(TypeSymbol type)
        {
            return IsInteger(type) || type == ScalarTypes.Dynamic;
        }

        private static bool IsSummable(TypeSymbol type)
        {
            return type is ScalarSymbol s && s.IsSummable;
        }

        private static bool IsTabular(TypeSymbol type)
        {
            return type != null && type.IsTabular;
        }

        private bool IsTabular(Expression expr)
        {
            return IsTabular(GetResultTypeOrError(expr));
        }

        private bool IsColumn(Expression expr)
        {
            return GetReferencedSymbol(expr) is ColumnSymbol;
        }

        private static bool IsDatabase(Symbol symbol)
        {
            return symbol is DatabaseSymbol;
        }

        private static bool IsCluster(Symbol symbol)
        {
            return symbol is ClusterSymbol;
        }

        private static SemanticInfo GetSemanticInfo(Symbol referencedSymbol, params Diagnostic[] diagnostics)
        {
            return CreateSemanticInfo(referencedSymbol, (IEnumerable<Diagnostic>)diagnostics);
        }

        private static SemanticInfo CreateSemanticInfo(Symbol referencedSymbol, IEnumerable<Diagnostic> diagnostics = null)
        {
            switch (referencedSymbol.Kind)
            {
                case SymbolKind.Operator:
                case SymbolKind.Column:
                case SymbolKind.Table:
                case SymbolKind.Database:
                case SymbolKind.Cluster:
                case SymbolKind.Parameter:
                case SymbolKind.Function:
                case SymbolKind.Pattern:
                case SymbolKind.Group:
                    return new SemanticInfo(referencedSymbol, GetResultType(referencedSymbol), diagnostics);
                case SymbolKind.Variable:
                    var v = (VariableSymbol)referencedSymbol;
                    return new SemanticInfo(referencedSymbol, GetResultType(referencedSymbol), diagnostics, isConstant: v.IsConstant);
                case SymbolKind.Scalar:
                case SymbolKind.Tuple:
                    return new SemanticInfo((TypeSymbol)referencedSymbol, diagnostics);
                default:
                    return new SemanticInfo(null, ErrorSymbol.Instance, diagnostics);
            }
        }

        private static TypeSymbol GetResultType(Symbol symbol)
        {
            return Symbol.GetExpressionResultType(symbol);
        }
#endregion

        #region Symbol assignability

        public static bool SymbolsAssignable(IReadOnlyList<TypeSymbol> parameterTypes, Symbol valueType, Conversion conversion = Conversion.None)
        {
            for (int i = 0; i < parameterTypes.Count; i++)
            {
                if (SymbolsAssignable(parameterTypes[i], valueType, conversion))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True if a value of type <see cref="P:valueType"/> can be assigned to a parameter of type <see cref="P:parameterType"/>
        /// </summary>
        public static bool SymbolsAssignable(Symbol parameterType, Symbol valueType, Conversion conversion = Conversion.None)
        {
            if (parameterType == valueType)
                return true;

            if (parameterType == null || valueType == null)
                return false;

            if (parameterType.Kind != valueType.Kind)
                return false;

            switch (parameterType.Kind)
            {
                case SymbolKind.Column:
                    var c1 = (ColumnSymbol)parameterType;
                    var c2 = (ColumnSymbol)valueType;
                    return c1.Name == c2.Name && SymbolsAssignable(c1.Type, c2.Type, conversion);

                case SymbolKind.Tuple:
                case SymbolKind.Group:
                    return MembersEqual(parameterType, valueType);

                case SymbolKind.Table:
                    return TablesAssignable((TableSymbol)parameterType, (TableSymbol)valueType);

                case SymbolKind.Scalar:
                    switch (conversion)
                    {
                        case Conversion.Promotable:
                            return IsPromotable((TypeSymbol)valueType, (TypeSymbol)parameterType);
                        case Conversion.Compatible:
                            return IsPromotable((TypeSymbol)valueType, (TypeSymbol)parameterType)
                                || IsPromotable((TypeSymbol)parameterType, (TypeSymbol)valueType);
                        case Conversion.Any:
                            return true;
                        default:
                            return false;
                    }
            }

            return false;
        }

        public static bool MembersEqual(Symbol symbol1, Symbol symbol2)
        {
            if (symbol1.Members.Count != symbol2.Members.Count)
                return false;

            for (int i = 0, n = symbol1.Members.Count; i < n; i++)
            {
                if (!SymbolsAssignable(symbol1.Members[i], symbol2.Members[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// True if a table value can be assigned to a parameter of a specific table type.
        /// </summary>
        private static bool TablesAssignable(TableSymbol parameterType, TableSymbol valueType)
        {
            // ensure that the value table has at least the columns specified for the parameter table.

            foreach (var parameterColumn in parameterType.Columns)
            {
                if (!valueType.TryGetColumn(parameterColumn.Name, out var valueColumn)
                    || !SymbolsAssignable(parameterColumn.Type, valueColumn.Type))
                {
                    return false;
                }
            }

            return true;
        }
#endregion

        #region Check methods
        private enum QueryParameterKind
        {
            /// <summary>
            /// The parameter type is the type specified.
            /// </summary>
            Fixed,

            /// <summary>
            /// Any scalar type
            /// </summary>
            Scalar,

            /// <summary>
            /// Any scalar integer type (int, long)
            /// </summary>
            Integer,

            /// <summary>
            /// Either real or decimal
            /// </summary>
            RealOrDecimal,

            /// <summary>
            /// Either a string or a dynamic value
            /// </summary>
            StringOrDynamic,

            /// <summary>
            /// Any scalar numeric type (int, long, real, decimal)
            /// </summary>
            Number,

            /// <summary>
            /// A scalar string literal
            /// </summary>
            StringLiteral,

            /// <summary>
            /// A scalar boolean literal
            /// </summary>
            BoolLiteral,

            /// <summary>
            /// Any scalar type that is summable (number, timespan, datetime)
            /// </summary>
            Summable,

            /// <summary>
            /// The parameter is an identifier token (or keyword)
            /// </summary>
            Identifier,

            /// <summary>
            /// The parameter is a name declaration
            /// </summary>
            NameDeclaration,

            /// <summary>
            /// The parameter is a list of name declarations
            /// </summary>
            NameDeclarationList,

            /// <summary>
            /// The parameter is a column reference
            /// </summary>
            Column,
        }

        private class QueryParameterInfo
        {
            public IReadOnlyList<string> Names { get; }

            public IReadOnlyList<QueryParameterValueInfo> ValueInfos { get; }

            public bool IsRepeatable { get; }

            public QueryParameterInfo(IReadOnlyList<string> names, IReadOnlyList<QueryParameterValueInfo> valueInfos, bool isRepeatable = false)
            {
                Names = names;
                ValueInfos = valueInfos.ToReadOnly();
                IsRepeatable = isRepeatable;
            }

            public QueryParameterInfo(string name, IReadOnlyList<QueryParameterValueInfo> valueInfos, bool isRepeatable = false)
                : this(new[] { name }, valueInfos, isRepeatable)
            {
            }

            public QueryParameterInfo(IReadOnlyList<string> names, QueryParameterKind kind, bool caseSensitive = true, IEnumerable<object> values = null, bool isRepeatable = false)
                : this(names, new[] { new QueryParameterValueInfo(kind, caseSensitive, values) }, isRepeatable)
            {
            }

            public QueryParameterInfo(IReadOnlyList<string> names, TypeSymbol fixedType, bool caseSensitive = true, IEnumerable<object> values = null, bool isRepeatable = false)
                : this(names, new[] { new QueryParameterValueInfo(fixedType, caseSensitive, values) }, isRepeatable)
            {
            }

            public QueryParameterInfo(string name, QueryParameterKind kind, bool caseSensitive = true, IEnumerable<object> values = null, bool isRepeatable = false)
                : this(new[] { name }, new[] { new QueryParameterValueInfo(kind, null, caseSensitive, values) }, isRepeatable)
            {
            }

            public QueryParameterInfo(string name, TypeSymbol fixedType, bool caseSensitive = true, IEnumerable<object> values = null, bool isRepeatable = false)
                : this(new[] { name }, new[] { new QueryParameterValueInfo(fixedType, caseSensitive, values) }, isRepeatable)
            {
            }
        }

        private class QueryParameterValueInfo
        {
            public QueryParameterKind Kind { get; }

            public TypeSymbol FixedType { get; }

            public bool CaseSensitive { get; }

            public IReadOnlyList<object> Values { get; }

            internal QueryParameterValueInfo(QueryParameterKind kind, TypeSymbol fixedType, bool caseSensitive, IEnumerable<object> values)
            {
                Kind = kind;
                FixedType = fixedType;
                CaseSensitive = caseSensitive;
                Values = values.ToReadOnly();
            }

            public QueryParameterValueInfo(QueryParameterKind kind, bool caseSensitive = true, IEnumerable<object> values = null)
                : this(kind, null, caseSensitive, values)
            {
            }

            public QueryParameterValueInfo(TypeSymbol fixedType, bool caseSensitive = true, IEnumerable<object> values = null)
                : this(QueryParameterKind.Fixed, fixedType, caseSensitive, values)
            {
            }
        }

        private void CheckQueryParameters(SyntaxList<NamedParameter> parameters, IReadOnlyList<QueryParameterInfo> queryParameters, List<Diagnostic> diagnostics)
        {
            var names = s_stringSetPool.AllocateFromPool();
            try
            {
                for (int i = 0, n = parameters.Count; i < n; i++)
                {
                    CheckQueryParameter(parameters[i], queryParameters, names, diagnostics);
                }
            }
            finally
            {
                s_stringSetPool.ReturnToPool(names);
            }
        }

        private void CheckQueryParameters(SyntaxList<SeparatedElement<NamedParameter>> parameters, IReadOnlyList<QueryParameterInfo> queryParameters, List<Diagnostic> diagnostics)
        {
            var names = s_stringSetPool.AllocateFromPool();
            try
            {
                for (int i = 0, n = parameters.Count; i < n; i++)
                {
                    CheckQueryParameter(parameters[i].Element, queryParameters, names, diagnostics);
                }
            }
            finally
            {
                s_stringSetPool.ReturnToPool(names);
            }
        }

        private void CheckQueryParameter(NamedParameter parameter, IReadOnlyList<QueryParameterInfo> queryParameters, HashSet<string> namesAlreadySpecified, List<Diagnostic> diagnostics)
        {
            var name = parameter.Name.SimpleName;
            if (!string.IsNullOrEmpty(name))
            {
                var info = GetQueryParameterInfo(name, queryParameters);

                if (info != null)
                {
                    foreach (var n in info.Names)
                    {
                        if (!info.IsRepeatable)
                        {
                            if (namesAlreadySpecified.Contains(n))
                            {
                                diagnostics.Add(DiagnosticFacts.GetParameterAlreadySpecified(name).WithLocation(parameter.Name));
                                break;
                            }
                            else
                            {
                                namesAlreadySpecified.Add(n);
                            }
                        }
                    }

                    CheckQueryParameter(parameter, info, diagnostics);
                }
                else
                {
                    diagnostics.Add(DiagnosticFacts.GetUnknownParameterName(name).WithLocation(parameter.Name));
                }
            }
        }

        private void CheckQueryParameter(NamedParameter parameter, QueryParameterInfo info, List<Diagnostic> diagnostics)
        {
            if (!IsAnyQueryParameterKind(parameter, info))
            {
                // generate error from first parameter kind
                var valueInfo = info.ValueInfos[0];

                switch (valueInfo.Kind)
                {
                    case QueryParameterKind.Fixed:
                        CheckIsType(parameter.Expression, valueInfo.FixedType, Conversion.Compatible, diagnostics);
                        break;
                    case QueryParameterKind.Integer:
                        CheckIsInteger(parameter.Expression, diagnostics);
                        break;
                    case QueryParameterKind.Number:
                        CheckIsNumber(parameter.Expression, diagnostics);
                        break;
                    case QueryParameterKind.RealOrDecimal:
                        CheckIsRealOrDecimal(parameter.Expression, diagnostics);
                        break;
                    case QueryParameterKind.Scalar:
                        CheckIsScalar(parameter.Expression, diagnostics);
                        break;
                    case QueryParameterKind.StringOrDynamic:
                        CheckIsStringOrDynamic(parameter.Expression, diagnostics);
                        break;
                    case QueryParameterKind.Summable:
                        CheckIsSummable(parameter.Expression, diagnostics);
                        break;
                    case QueryParameterKind.StringLiteral:
                        CheckIsLiteral(parameter.Expression, diagnostics);
                        CheckIsExactType(parameter.Expression, ScalarTypes.String, diagnostics);
                        break;
                    case QueryParameterKind.BoolLiteral:
                        CheckIsLiteral(parameter.Expression, diagnostics);
                        CheckIsExactType(parameter.Expression, ScalarTypes.Bool, diagnostics);
                        break;
                    case QueryParameterKind.Column:
                        CheckIsColumn(parameter.Expression, diagnostics);
                        break;
                    case QueryParameterKind.Identifier:
                        CheckIsTokenLiteral(parameter.Expression, valueInfo.Values, valueInfo.CaseSensitive, diagnostics);
                        break;
                }

                if (valueInfo.Kind != QueryParameterKind.Identifier && valueInfo.Values != null && valueInfo.Values.Count > 0)
                {
                    CheckIsLiteral(parameter.Expression, diagnostics);
                    CheckLiteralValue(parameter.Expression, valueInfo.Values, valueInfo.CaseSensitive, diagnostics);
                }
            }
        }

        private bool IsAnyQueryParameterKind(NamedParameter parameter, QueryParameterInfo info)
        {
            var type = GetResultTypeOrError(parameter.Expression);

            foreach (var valueInfo in info.ValueInfos)
            {
                if (IsQueryParameterKind(parameter, valueInfo))
                    return true;
            }

            return false;
        }

        private bool IsQueryParameterKind(NamedParameter parameter, QueryParameterValueInfo valueInfo)
        {
            var type = GetResultTypeOrError(parameter.Expression);
            switch (valueInfo.Kind)
            {
                case QueryParameterKind.Fixed:
                    if (!IsType(parameter.Expression, valueInfo.FixedType, Conversion.Compatible))
                        return false;
                    break;
                case QueryParameterKind.Integer:
                    if (!IsInteger(type))
                        return false;
                    break;
                case QueryParameterKind.Number:
                    if (!IsNumber(type))
                        return false;
                    break;
                case QueryParameterKind.RealOrDecimal:
                    if (!IsRealOrDecimal(type))
                        return false;
                    break;
                case QueryParameterKind.Scalar:
                    if (!type.IsScalar)
                        return false;
                    break;
                case QueryParameterKind.StringOrDynamic:
                    if (!IsStringOrDynamic(type))
                        return false;
                    break;
                case QueryParameterKind.Summable:
                    if (!IsSummable(type))
                        return false;
                    break;
                case QueryParameterKind.StringLiteral:
                    if (!(parameter.Expression.IsLiteral && IsType(parameter.Expression, ScalarTypes.String)))
                        return false;
                    break;
                case QueryParameterKind.BoolLiteral:
                    if (!(parameter.Expression.IsLiteral && IsType(parameter.Expression, ScalarTypes.Bool)))
                        return false;
                    break;
                case QueryParameterKind.Column:
                    if (!(GetReferencedSymbol(parameter.Expression) is ColumnSymbol))
                        return false;
                    break;
                case QueryParameterKind.Identifier:
                    if (!IsTokenLiteral(parameter.Expression, valueInfo.Values, valueInfo.CaseSensitive))
                        return false;
                    break;
            }

            if (valueInfo.Kind != QueryParameterKind.Identifier && valueInfo.Values != null && valueInfo.Values.Count > 0)
            {
                if (!parameter.Expression.IsLiteral)
                    return false;

                if (!IsLiteralValue(parameter.Expression, valueInfo.Values, valueInfo.CaseSensitive))
                    return false;
            }

            return true;
        }

        private static QueryParameterInfo GetQueryParameterInfo(string name, IReadOnlyList<QueryParameterInfo> parameters)
        {
            for (int i = 0, n = parameters.Count; i < n; i++)
            {
                if (parameters[i].Names.Contains(name))
                {
                    return parameters[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Checks that the data value expressions have the types corresponding to the columns.
        /// </summary>
        private void CheckDataValueTypes(SyntaxList<SeparatedElement<Expression>> expressions, List<ColumnSymbol> columns, List<Diagnostic> diagnostics)
        {
            if (expressions.Count % columns.Count != 0)
            {
                diagnostics.Add(DiagnosticFacts.GetIncorrectNumberOfDataValues(columns.Count).WithLocation(expressions));
            }

            for (int i = 0, n = expressions.Count; i < n; i++)
            {
                var expr = expressions[i].Element;
                CheckIsScalar(expr, diagnostics);

                // note: data values are convertible at runtime so no check is given
                // consider adding checks for obvious incovertible values
                // var column = columns[i % columns.Count];
                // CheckIsType(expr, column.Type, true, diagnostics);
            }
        }

        private bool CheckIsScalar(Expression expression, List<Diagnostic> diagnostics, Symbol resultType = null)
        {
            if (resultType == null)
                resultType = GetResultType(expression);

            if (resultType != null)
            {
                if (resultType.IsScalar)
                    return true;

                if (!resultType.IsError)
                {
                    diagnostics.Add(DiagnosticFacts.GetScalarTypeExpected().WithLocation(expression));
                }
            }

            return false;
        }

        private bool CheckIsInteger(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsInteger(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeInteger().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsRealOrDecimal(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsRealOrDecimal(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeRealOrDecimal().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsIntegerOrDynamic(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsIntegerOrDynamic(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeIntegerOrDynamic().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsStringOrDynamic(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsStringOrDynamic(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustHaveType(ScalarTypes.String, ScalarTypes.Dynamic).WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsNumber(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsNumber(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeNumeric().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsSummable(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsSummable(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeSummable().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsExactType(Expression expression, TypeSymbol type, List<Diagnostic> diagnostics)
        {
            return CheckIsType(expression, type, Conversion.None, diagnostics);
        }

        private bool CheckIsTypeOrDynamic(Expression expression, TypeSymbol type, bool canPromote, List<Diagnostic> diagnostics)
        {
            var resultType = GetResultTypeOrError(expression);

            if (SymbolsAssignable(resultType, type) || (canPromote && IsPromotable(resultType, type)) || SymbolsAssignable(resultType, ScalarTypes.Dynamic))
                return true;

            if (!resultType.IsError)
            {
                if (SymbolsAssignable(type, ScalarTypes.Dynamic))
                {
                    diagnostics.Add(DiagnosticFacts.GetExpressionMustHaveType(type).WithLocation(expression));
                }
                else
                {
                    diagnostics.Add(DiagnosticFacts.GetExpressionMustHaveType(type, ScalarTypes.Dynamic).WithLocation(expression));
                }
            }

            return false;
        }

        private bool IsType(Expression expression, TypeSymbol type, Conversion conversionKind = Conversion.None)
        {
            var resultType = GetResultTypeOrError(expression);
            return SymbolsAssignable(resultType, type, conversionKind);
        }

        private bool CheckIsType(Expression expression, TypeSymbol type, Conversion conversionKind, List<Diagnostic> diagnostics)
        {
            if (IsType(expression, type, conversionKind))
                return true;

            if (!GetResultTypeOrError(expression).IsError && !type.IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustHaveType(type).WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsNotType(Expression expression, Symbol type, List<Diagnostic> diagnostics)
        {
            if (!SymbolsAssignable(GetResultTypeOrError(expression), type))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetTypeNotAllowed(type).WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsIntervalType(Expression expression, TypeSymbol rangeType, List<Diagnostic> diagnostics)
        {
            // check to see if add operator is defined between the expression's type and the range type
            var info = GetBinaryOperatorInfo(OperatorKind.Add, expression, rangeType, expression, GetResultTypeOrError(expression), expression);
            if (info.ReferencedSymbol != null && SymbolsAssignable(rangeType, info.ResultType))
                return true;

            if (!rangeType.IsError && !GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetTypeIsNotIntervalType(GetResultTypeOrError(expression), rangeType).WithLocation(expression));
            }

            return false;
        }

        private bool IsLiteralOrName(Expression expression)
        {
            return expression is LiteralExpression ||
                expression is CompoundStringLiteralExpression ||
                expression.Kind == SyntaxKind.DynamicExpression ||
                expression.Kind == SyntaxKind.NameReference;
        }

        private bool CheckIsIdentifierNameDeclaration(NameDeclaration name, List<Diagnostic> diagnostics)
        {
            if (name.Name is TokenName)
                return true;

            diagnostics.Add(DiagnosticFacts.GetIdentifierNameOnly().WithLocation(name));
            return false;
        }

        private bool CheckIsLiteralOrName(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsLiteralOrName(expression))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeConstantOrIdentifier().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsTabular(Expression expression, List<Diagnostic> diagnostics, Symbol resultType = null)
        {
            resultType = resultType ?? GetResultType(expression);

            if (resultType != null)
            {
                if (resultType.IsTabular)
                    return true;

                if (!resultType.IsError)
                {
                    diagnostics.Add(DiagnosticFacts.GetTableExpected().WithLocation(expression));
                }
            }

            return false;
        }

        private bool CheckIsSingleColumnTable(Expression expression, List<Diagnostic> diagnostics, Symbol resultType = null)
        {
            resultType = resultType ?? GetResultType(expression);

            if (resultType != null)
            {
                var table = resultType as TableSymbol;
                if (table != null && table.Columns.Count == 1)
                    return true;

                if (!resultType.IsError)
                {
                    diagnostics.Add(DiagnosticFacts.GetSingleColumnTableExpected().WithLocation(expression));
                }
            }

            return false;
        }


        private bool CheckIsDatabase(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsDatabase(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetDatabaseExpected().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsCluster(Expression expression, List<Diagnostic> diagnostics)
        {
            if (IsCluster(GetResultTypeOrError(expression)))
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetClusterExpected().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsColumn(Expression expression, List<Diagnostic> diagnostics)
        {
            if (GetReferencedSymbol(expression) is ColumnSymbol)
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetColumnExpected().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsLiteral(Expression expression, List<Diagnostic> diagnostics)
        {
            if (expression.IsLiteral)
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeLiteral().WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsLiteralValue(Expression expression, List<Diagnostic> diagnostics)
        {
            if (expression.IsLiteral && expression.Kind != SyntaxKind.TokenLiteralExpression)
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeLiteralScalarValue().WithLocation(expression));
            }

            return false;
        }

        private bool CheckLiteralStringNotEmpty(Expression expression, List<Diagnostic> diagnostics)
        {
            var result = GetResultTypeOrError(expression);
            if (!result.IsError && expression.IsLiteral)
            {
                string value = expression.LiteralValue?.ToString();
                if (!string.IsNullOrEmpty(value))
                    return true;

                diagnostics.Add(DiagnosticFacts.GetExpressionMustNotBeEmpty().WithLocation(expression));
            }

            return false;
        }

        private bool IsTokenLiteral(Expression expression, IReadOnlyList<object> values, bool caseSensitive)
        {
            if (expression.Kind == SyntaxKind.TokenLiteralExpression)
            {
                if (values != null && values.Count > 0)
                {
                    object value = ConvertHelper.ChangeType(expression.LiteralValue, values[0]);
                    return Contains(values, value, caseSensitive);
                }

                return true;
            }

            return false;
        }

        private bool CheckIsTokenLiteral(Expression expression, IReadOnlyList<object> values, bool caseSensitive, List<Diagnostic> diagnostics)
        {
            var result = GetResultTypeOrError(expression);
            if (!result.IsError)
            {
                if (IsTokenLiteral(expression, values, caseSensitive))
                    return true;

                diagnostics.Add(DiagnosticFacts.GetTokenExpected(values.Select(v => v.ToString()).ToList()).WithLocation(expression));
            }

            return false;
        }

        private bool CheckIsToken(SyntaxToken token, IReadOnlyList<object> values, bool caseSensitive, List<Diagnostic> diagnostics)
        {
            var value = ConvertHelper.ChangeType(token.Text, values[0]);
            if (Contains(values, value, caseSensitive))
                return true;

            if (!token.HasSyntaxDiagnostics)
            {
                diagnostics.Add(DiagnosticFacts.GetTokenExpected(values.Select(v => v.ToString()).ToList()).WithLocation(token));
            }

            return false;
        }

        private bool IsLiteralValue(Expression expression, IReadOnlyList<object> values, bool caseSensitive)
        {
            if (!expression.IsLiteral)
                return false;

            object value = ConvertHelper.ChangeType(expression.LiteralValue, values[0]);
            return Contains(values, value, caseSensitive);
        }

        private bool CheckLiteralValue(Expression expression, IReadOnlyList<object> values, bool caseSensitive, List<Diagnostic> diagnostics)
        {
            var result = GetResultTypeOrError(expression);
            if (!result.IsError)
            {
                if (IsLiteralValue(expression, values, caseSensitive))
                    return true;

                diagnostics.Add(DiagnosticFacts.GetExpressionMustHaveValue(values).WithLocation(expression));
            }

            return false;
        }

        private static bool Contains(IReadOnlyList<object> values, object value, bool caseSensitive)
        {
            var stringValue = value as string;
            if (stringValue != null)
            {
                for (int i = 0, n = values.Count; i < n; i++)
                {
                    var v = values[i] as string;
                    if (string.Compare(stringValue, v, ignoreCase: !caseSensitive) == 0)
                        return true;
                }
            }
            else
            {
                for (int i = 0, n = values.Count; i < n; i++)
                {
                    if (values[i] == value)
                        return true;
                }
            }

            return false;
        }

        private bool CheckIsConstant(Expression expression, List<Diagnostic> diagnostics)
        {
            if (GetIsConstant(expression) 
                || expression.ReferencedSymbol is ParameterSymbol) // parameters might be constant
                return true;

            if (!GetResultTypeOrError(expression).IsError)
            {
                diagnostics.Add(DiagnosticFacts.GetExpressionMustBeConstant().WithLocation(expression));
            }

            return false;
        }

        /// <summary>
        /// True if any argument type is an error type.
        /// </summary>
        private static bool ArgumentsHaveErrors(IReadOnlyList<TypeSymbol> argumentTypes)
        {
            for (int i = 0; i < argumentTypes.Count; i++)
            {
                if (argumentTypes[i].IsError)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks the invocation of a method/operator signature
        /// </summary>
        private void CheckSignature(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes, SyntaxElement location, List<Diagnostic> dx)
        {
            var argCount = arguments.Count;
            int initialDxCount = dx.Count;

            if (!signature.IsValidArgumentCount(argCount))
            {
                if (signature.HasRepeatableParameters)
                {
                    if (argCount < signature.MinArgumentCount || argCount > signature.MaxArgumentCount)
                    {
                        dx.Add(DiagnosticFacts.GetFunctionExpectsArgumentCountRange(signature.Symbol.Name, signature.MinArgumentCount, signature.MaxArgumentCount).WithLocation(location));
                    }
                    else
                    {
                        // not sure how else to say this.. the variable arguments are not specified correctly?
                        dx.Add(DiagnosticFacts.GetFunctionHasIncorrectNumberOfArguments().WithLocation(location));
                    }
                }
                else if (argCount != signature.Parameters.Count)
                {
                    dx.Add(DiagnosticFacts.GetFunctionExpectsArgumentCountExact(signature.Symbol.Name, signature.Parameters.Count).WithLocation(location));
                }
            }

            var namedArgumentsAllowed = NamedArgumentsAllowed(signature);

            // check named arguments
            if (namedArgumentsAllowed && dx.Count == initialDxCount)
            {
                bool hadOutOfOrderNamedArgument = false;
                bool reportedUnnamedArgument = false;

                for (int i = 0; i < argCount; i++)
                {
                    var argument = arguments[i];
                    var parameter = signature.GetParameter(argument, i, argCount);

                    bool isNamed = IsNamedArgument(argument);
                    var argumentName = GetNamedArgumentName(argument);

                    if (isNamed && parameter == null)
                    {
                        dx.Add(DiagnosticFacts.GetUnknownArgumentName().WithLocation(argumentName));
                    }

                    if (isNamed && !hadOutOfOrderNamedArgument)
                    {
                        var orderedParameter = signature.GetParameter(i, argCount);
                        hadOutOfOrderNamedArgument = orderedParameter != parameter;
                    }
                    else if (!isNamed && hadOutOfOrderNamedArgument && !reportedUnnamedArgument)
                    {
                        dx.Add(DiagnosticFacts.GetUnnamedArgumentAfterOutofOrderNamedArgument().WithLocation(argument));
                        reportedUnnamedArgument = true;
                    }
                }
            }

            // check arguments... 
            if (dx.Count == initialDxCount)
            {
                for (int i = 0; i < argCount; i++)
                {
                    CheckArgument(signature, arguments, argumentTypes, i, dx);
                }
            }

            // check for missing arguments to non-optional parameters
            if (namedArgumentsAllowed && dx.Count == initialDxCount)
            {
                foreach (var parameter in signature.Parameters)
                {
                    if (!parameter.IsOptional)
                    {
                        var iArg = signature.GetArgumentIndex(parameter, arguments);
                        if (iArg < 0)
                        {
                            dx.Add(DiagnosticFacts.GetMissingArgumentForParameter(parameter.Name).WithLocation(location));
                        }
                    }
                }
            }
        }

        private static bool IsNamedArgument(Expression argument)
        {
            return argument is NamedExpression;
        }

        private static SyntaxNode GetNamedArgumentName(Expression argument)
        {
            switch (argument)
            {
                case SimpleNamedExpression sn:
                    return sn.Name;
                case CompoundNamedExpression cn:
                    return cn.Names;
                default:
                    return null;
            }
        }

        /// <summary>
        /// True if named arguments are allowed for this signature.
        /// </summary>
        private bool NamedArgumentsAllowed(Signature signature)
        {
            var fn = signature.Symbol as FunctionSymbol;
            return fn != null && !_globals.IsBuiltInFunction(fn);
        }

        private bool AllowLooseParameterMatching(Signature signature)
        {
            return signature.Symbol is FunctionSymbol fs
                && (_globals.IsDatabaseFunction(fs)
                || fs.Signatures[0].Declaration != null); // user function have declarations
        }

        private void CheckArgument(Signature signature, IReadOnlyList<Expression> arguments, IReadOnlyList<TypeSymbol> argumentTypes, int argumentIndex, List<Diagnostic> diagnostics)
        {
            var argument = arguments[argumentIndex];
            var argumentType = argumentTypes[argumentIndex];
            var parameter = GetParameter(signature, arguments, argumentIndex);

            if (parameter != null)
            {
                if (argument is StarExpression && signature.Symbol.Kind != SymbolKind.Operator)
                {
                    if (parameter.ArgumentKind != ArgumentKind.Star)
                    {
                        diagnostics.Add(DiagnosticFacts.GetStarExpressionNotAllowed().WithLocation(argument));
                    }
                    else if (argumentIndex < argumentTypes.Count - 1)
                    {
                        diagnostics.Add(DiagnosticFacts.GetStarExpressionMustBeLastArgument().WithLocation(argument));
                    }
                }
                else
                {
                    if (argument is CompoundNamedExpression cn)
                    {
                        diagnostics.Add(DiagnosticFacts.GetCompoundNamedArgumentsNotSupported().WithLocation(cn.Names));
                    }

                    // see through any named argument
                    if (argument is SimpleNamedExpression sn)
                    {
                        argument = sn.Expression;
                    }

                    switch (parameter.TypeKind)
                    {
                        case ParameterTypeKind.Declared:
                            switch (GetParameterMatchKind(signature, arguments, argumentTypes, argumentIndex))
                            {
                                case MatchKind.Compatible:
                                case MatchKind.None:
                                    if (!AllowLooseParameterMatching(signature))
                                    {
                                        diagnostics.Add(DiagnosticFacts.GetTypeExpected(parameter.DeclaredTypes).WithLocation(argument));
                                    }
                                    break;
                            }
                            break;

                        case ParameterTypeKind.Scalar:
                            CheckIsScalar(argument, diagnostics, argumentType);
                            break;

                        case ParameterTypeKind.Integer:
                            CheckIsInteger(argument, diagnostics);
                            break;

                        case ParameterTypeKind.RealOrDecimal:
                            CheckIsRealOrDecimal(argument, diagnostics);
                            break;

                        case ParameterTypeKind.IntegerOrDynamic:
                            CheckIsIntegerOrDynamic(argument, diagnostics);
                            break;

                        case ParameterTypeKind.StringOrDynamic:
                            CheckIsStringOrDynamic(argument, diagnostics);
                            break;

                        case ParameterTypeKind.Number:
                            CheckIsNumber(argument, diagnostics);
                            break;

                        case ParameterTypeKind.Summable:
                            CheckIsSummable(argument, diagnostics);
                            break;

                        case ParameterTypeKind.NotBool:
                            if (CheckIsScalar(argument, diagnostics))
                            {
                                CheckIsNotType(argument, ScalarTypes.Bool, diagnostics);
                            }
                            break;

                        case ParameterTypeKind.NotRealOrBool:
                            if (CheckIsScalar(argument, diagnostics))
                            {
                                CheckIsNotType(argument, ScalarTypes.Real, diagnostics);
                                CheckIsNotType(argument, ScalarTypes.Bool, diagnostics);
                            }
                            break;

                        case ParameterTypeKind.NotDynamic:
                            if (CheckIsScalar(argument, diagnostics))
                            {
                                CheckIsNotType(argument, ScalarTypes.Dynamic, diagnostics);
                            }
                            break;

                        case ParameterTypeKind.Tabular:
                            CheckIsTabular(argument, diagnostics, argumentType);
                            break;

                        case ParameterTypeKind.SingleColumnTable:
                            CheckIsSingleColumnTable(argument, diagnostics, argumentType);
                            break;

                        case ParameterTypeKind.Database:
                            CheckIsDatabase(argument, diagnostics);
                            break;

                        case ParameterTypeKind.Cluster:
                            CheckIsCluster(argument, diagnostics);
                            break;

                        case ParameterTypeKind.Parameter0:
                            CheckIsExactType(argument, argumentTypes[0], diagnostics);
                            break;

                        case ParameterTypeKind.Parameter1:
                            CheckIsExactType(argument, argumentTypes[1], diagnostics);
                            break;

                        case ParameterTypeKind.Parameter2:
                            CheckIsExactType(argument, argumentTypes[2], diagnostics);
                            break;

                        case ParameterTypeKind.CommonScalar:
                            if (CheckIsScalar(argument, diagnostics))
                            {
                                var commonType = GetCommonArgumentType(signature, arguments, argumentTypes);
                                if (commonType != null)
                                {
                                    CheckIsType(argument, commonType, Conversion.Promotable, diagnostics);
                                }
                            }
                            break;

                        case ParameterTypeKind.CommonScalarOrDynamic:
                            if (CheckIsScalar(argument, diagnostics))
                            {
                                var commonType = GetCommonArgumentType(signature, arguments, argumentTypes);
                                if (commonType != null)
                                {
                                    CheckIsTypeOrDynamic(argument, commonType, true, diagnostics);
                                }
                            }
                            break;

                        case ParameterTypeKind.CommonNumber:
                            if (CheckIsNumber(argument, diagnostics))
                            {
                                var commonType = GetCommonArgumentType(signature, arguments, argumentTypes);
                                if (commonType != null)
                                {
                                    CheckIsType(argument, commonType, Conversion.Promotable, diagnostics);
                                }
                            }
                            break;

                        case ParameterTypeKind.CommonSummable:
                            if (CheckIsSummable(argument, diagnostics))
                            {
                                var commonType = GetCommonArgumentType(signature, arguments, argumentTypes);
                                if (commonType != null)
                                {
                                    CheckIsType(argument, commonType, Conversion.Promotable, diagnostics);
                                }
                            }
                            break;
                    }

                    switch (parameter.ArgumentKind)
                    {
                        case ArgumentKind.Column:
                            CheckIsColumn(argument, diagnostics);
                            break;

                        case ArgumentKind.Constant:
                            CheckIsConstant(argument, diagnostics);
                            break;

                        case ArgumentKind.Literal:
                            if (CheckIsLiteral(argument, diagnostics) && parameter.Values.Count > 0)
                            {
                                CheckLiteralValue(argument, parameter.Values, parameter.IsCaseSensitive, diagnostics);
                            }
                            break;

                        case ArgumentKind.LiteralNotEmpty:
                            if (CheckIsLiteral(argument, diagnostics))
                            {
                                CheckLiteralStringNotEmpty(argument, diagnostics);
                            }
                            break;
                    }
                }
            }
        }

        private static bool CheckArgumentCount(SyntaxList<SeparatedElement<Expression>> expressions, int expectedCount, List<Diagnostic> diagnostics)
        {
            if (expressions.Count == expectedCount)
                return true;

            diagnostics.Add(DiagnosticFacts.GetArgumentCountExpected(expectedCount).WithLocation(expressions));
            return false;
        }

// keep line for BRIDGE.NET bug
#endregion
    }
}