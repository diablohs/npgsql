﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Common.Logging;
using Npgsql.TypeHandlers;
using NpgsqlTypes;
using System.Diagnostics.Contracts;

namespace Npgsql
{
    internal class TypeHandlerRegistry
    {
        #region Members

        internal NpgsqlConnector Connector { get; private set; }
        internal TypeHandler UnrecognizedTypeHandler { get; private set; }

        readonly Dictionary<uint, TypeHandler> _oidIndex;
        readonly Dictionary<DbType, TypeHandler> _byDbType;
        readonly Dictionary<NpgsqlDbType, TypeHandler> _byNpgsqlDbType;
        readonly Dictionary<Type, TypeHandler> _byType;
        Dictionary<Type, TypeHandler> _byEnumTypeAsArray;
        List<BackendType> _backendTypes;

        static internal readonly Dictionary<string, TypeAndMapping> HandlerTypes;
        static readonly Dictionary<NpgsqlDbType, DbType> NpgsqlDbTypeToDbType;
        static readonly Dictionary<DbType, NpgsqlDbType> DbTypeToNpgsqlDbType;
        static readonly Dictionary<Type, NpgsqlDbType> TypeToNpgsqlDbType;
        static readonly Dictionary<Type, DbType> TypeToDbType;

        /// <summary>
        /// Caches, for each connection string, the results of the backend type query in the form of a list of type
        /// info structs keyed by the PG name.
        /// Repeated connections to the same connection string reuse the query results and avoid an additional
        /// roundtrip at open-time.
        /// </summary>
        static readonly ConcurrentDictionary<string, List<BackendType>> BackendTypeCache = new ConcurrentDictionary<string, List<BackendType>>();

        static ConcurrentDictionary<string, TypeHandler> _globalEnumRegistrations;

        static readonly ILog Log = LogManager.GetCurrentClassLogger();

        #endregion

        #region Initialization and Loading

        static internal void Setup(NpgsqlConnector connector)
        {
            connector.TypeHandlerRegistry = new TypeHandlerRegistry(connector);

            List<BackendType> types;
            if (!BackendTypeCache.TryGetValue(connector.ConnectionString, out types)) {
                types = BackendTypeCache[connector.ConnectionString] = LoadBackendTypes(connector);
            }

            connector.TypeHandlerRegistry.RegisterTypes(types);
        }

        TypeHandlerRegistry(NpgsqlConnector connector)
        {
            Connector = connector;
            _oidIndex = new Dictionary<uint, TypeHandler>();
            _byDbType = new Dictionary<DbType, TypeHandler>();
            _byNpgsqlDbType = new Dictionary<NpgsqlDbType, TypeHandler>();
            _byType = new Dictionary<Type, TypeHandler>();
            UnrecognizedTypeHandler = new UnrecognizedTypeHandler();
        }

        static List<BackendType> LoadBackendTypes(NpgsqlConnector connector)
        {
            // Select all basic, range and enum types along with their array type's OID and text delimiter
            // For range types, select also the subtype they contain
            string query = connector.SupportsRangeTypes ?
                                 @"SELECT typname, pg_type.oid, typtype, typarray, rngsubtype " +
                                 @"FROM pg_type " +
                                 @"LEFT OUTER JOIN pg_range ON (pg_type.oid = pg_range.rngtypid) " +
                                 @"WHERE typtype IN ('b', 'r', 'e') " +
                                 @"ORDER BY typtype"
                                 :
                                 @"SELECT typname, pg_type.oid, typtype, typarray " +
                                 @"FROM pg_type " +
                                 @"WHERE typtype IN ('b', 'e') " +
                                 @"ORDER BY typtype";

            var types = new List<BackendType>();
            using (var command = new NpgsqlCommand(query, connector))
            {
                command.AllResultTypesAreUnknown = true;
                using (var dr = command.Execute(CommandBehavior.SequentialAccess))
                {
                    while (dr.Read())
                    {
                        var name = dr.GetString(0);
                        Contract.Assume(name != null);
                        var oid = Convert.ToUInt32(dr[1]);
                        Contract.Assume(oid != 0);
                        var type = (BackendTypeType) dr.GetString(2)[0];
                        var arrayOID = Convert.ToUInt32(dr[3]);
                        var rangeSubtypeOID = type == BackendTypeType.Range
                          ? UInt32.Parse(dr.GetString(4))
                          : 0;

                        types.Add(new BackendType
                        {
                            Name = name,
                            OID = oid,
                            Type = type,
                            ArrayOID = arrayOID,
                            RangeSubtypeOID = rangeSubtypeOID,
                        });
                    }
                }
            }

            /*foreach (var notFound in _typeHandlers.Where(t => t.Oid == -1)) {
                _log.WarnFormat("Could not find type {0} in pg_type", notFound.PgNames[0]);
            }*/

            return types;
        }

        void RegisterTypes(List<BackendType> backendTypes)
        {
            foreach (var backendType in backendTypes)
            {
                switch (backendType.Type) {
                case BackendTypeType.Base:
                    RegisterBaseType(backendType);
                    continue;
                case BackendTypeType.Range:
                    RegisterRangeType(backendType);
                    continue;
                case BackendTypeType.Enum:
                    TypeHandler handler;
                    if (_globalEnumRegistrations != null && _globalEnumRegistrations.TryGetValue(backendType.Name, out handler)) {
                        ActivateEnumType(handler, backendType);
                    }
                    continue;
                default:
                    Log.Error("Unknown type of type encountered, skipping: " + backendType);
                    continue;
                }
            }

            _backendTypes = backendTypes;
        }

        void RegisterBaseType(BackendType backendType)
        {
            TypeAndMapping typeAndMapping;
            if (!HandlerTypes.TryGetValue(backendType.Name, out typeAndMapping)) {
                // Backend type not supported by Npgsql
                return;
            }

            var handlerType = typeAndMapping.HandlerType;
            var mapping = typeAndMapping.Mapping;

            // Instantiate the type handler. If it has a constructor that accepts an NpgsqlConnector, use that to allow
            // the handler to make connector-specific adjustments. Otherwise (the normal case), use the default constructor.
            var handler = (TypeHandler)(
                handlerType.GetConstructor(new[] { typeof(TypeHandlerRegistry) }) != null
                    ? Activator.CreateInstance(handlerType, this)
                    : Activator.CreateInstance(handlerType)
            );

            handler.OID = backendType.OID;
            _oidIndex[backendType.OID] = handler;
            handler.PgName = backendType.Name;

            if (mapping.NpgsqlDbType.HasValue)
            {
                var npgsqlDbType = mapping.NpgsqlDbType.Value;
                if (_byNpgsqlDbType.ContainsKey(npgsqlDbType))
                    throw new Exception(String.Format("Two type handlers registered on same NpgsqlDbType {0}: {1} and {2}",
                                        npgsqlDbType, _byNpgsqlDbType[npgsqlDbType].GetType().Name, handlerType.Name));
                _byNpgsqlDbType[npgsqlDbType] = handler;
                handler.NpgsqlDbType = npgsqlDbType;
            }

            foreach (var dbType in mapping.DbTypes)
            {
                if (_byDbType.ContainsKey(dbType))
                    throw new Exception(String.Format("Two type handlers registered on same DbType {0}: {1} and {2}",
                                        dbType, _byDbType[dbType].GetType().Name, handlerType.Name));
                _byDbType[dbType] = handler;
            }

            foreach (var type in mapping.Types)
            {
                if (_byType.ContainsKey(type))
                    throw new Exception(String.Format("Two type handlers registered on same .NET type {0}: {1} and {2}",
                                        type, _byType[type].GetType().Name, handlerType.Name));
                _byType[type] = handler;
            }

            if (backendType.ArrayOID != 0) {
                var arrayHandler = RegisterArrayType(backendType.ArrayOID, handler);
                _byNpgsqlDbType.Add(NpgsqlDbType.Array | handler.NpgsqlDbType, arrayHandler);
            }
        }

        #endregion

        #region Array

        TypeHandler RegisterArrayType(uint oid, TypeHandler elementHandler)
        {
            ArrayHandler arrayHandler;

            var asBitStringHandler = elementHandler as BitStringHandler;
            if (asBitStringHandler != null) {
                // BitString requires a special array handler which returns bool or BitArray
                arrayHandler = new BitStringArrayHandler(asBitStringHandler);
            } else if (elementHandler is ITypeHandlerWithPsv) {
                var arrayHandlerType = typeof(ArrayHandlerWithPsv<,>).MakeGenericType(elementHandler.GetFieldType(), elementHandler.GetProviderSpecificFieldType());
                arrayHandler = (ArrayHandler)Activator.CreateInstance(arrayHandlerType, elementHandler);
            } else {
                var arrayHandlerType = typeof(ArrayHandler<>).MakeGenericType(elementHandler.GetFieldType());
                arrayHandler = (ArrayHandler)Activator.CreateInstance(arrayHandlerType, elementHandler);
            }

            arrayHandler.PgName = "array";
            arrayHandler.OID = oid;
            _oidIndex[oid] = arrayHandler;

            return arrayHandler;
        }

        #endregion

        #region Range

        void RegisterRangeType(BackendType backendType)
        {
            Contract.Requires(backendType.RangeSubtypeOID != 0);

            TypeHandler elementHandler;
            if (!_oidIndex.TryGetValue(backendType.RangeSubtypeOID, out elementHandler))
            {
                Log.ErrorFormat("Range type '{0}' refers to unknown subtype with OID {1}, skipping", backendType.Name, backendType.RangeSubtypeOID);
                return;
            }

            var rangeHandlerType = typeof(RangeHandler<>).MakeGenericType(elementHandler.GetFieldType());
            var rangeType = typeof(NpgsqlRange<>).MakeGenericType(elementHandler.GetFieldType());
            var handler = (TypeHandler)Activator.CreateInstance(rangeHandlerType, elementHandler, backendType.Name);

            handler.PgName = backendType.Name;
            handler.NpgsqlDbType = NpgsqlDbType.Range | elementHandler.NpgsqlDbType;
            handler.OID = backendType.OID;
            _oidIndex[backendType.OID] = handler;
            _byNpgsqlDbType.Add(handler.NpgsqlDbType, handler);

            RegisterArrayType(backendType.ArrayOID, handler);
        }

        #endregion

        #region Enum

        internal void RegisterEnumType<TEnum>(string pgName) where TEnum : struct
        {
            var backendTypeInfo = _backendTypes.FirstOrDefault(t => t.Name == pgName);
            if (backendTypeInfo == null) {
                throw new Exception(String.Format("An enum with the name {0} was not found in the database", pgName));
            }

            var handler = new EnumHandler<TEnum>();
            ActivateEnumType(handler, backendTypeInfo);
        }

        internal static void RegisterEnumTypeGlobally<TEnum>(string pgName) where TEnum : struct
        {
            if (_globalEnumRegistrations == null) {
                _globalEnumRegistrations = new ConcurrentDictionary<string, TypeHandler>();
            }

            _globalEnumRegistrations[pgName] = new EnumHandler<TEnum>();
        }

        void ActivateEnumType(TypeHandler handler, BackendType backendType)
        {
            handler.PgName = backendType.Name;
            handler.OID = backendType.OID;
            handler.NpgsqlDbType = NpgsqlDbType.Enum;
            _oidIndex[backendType.OID] = handler;
            _byType[handler.GetFieldType()] = handler;

            if (backendType.ArrayOID != 0)
            {
                var arrayHandler = RegisterArrayType(backendType.ArrayOID, handler);
                if (_byEnumTypeAsArray == null) {
                    _byEnumTypeAsArray = new Dictionary<Type, TypeHandler>();
                }
                var enumType = handler.GetType().GetGenericArguments()[0];
                Contract.Assert(enumType.IsEnum);
                _byEnumTypeAsArray[enumType] = arrayHandler;
            }
        }

        #endregion

        #region Lookups

        /// <summary>
        /// Looks up a type handler by its Postgresql type's OID.
        /// </summary>
        /// <param name="oid">A Postgresql type OID</param>
        /// <returns>A type handler that can be used to encode and decode values.</returns>
        internal TypeHandler this[uint oid]
        {
            get
            {
                TypeHandler result;
                if (!_oidIndex.TryGetValue(oid, out result)) {
                    result = UnrecognizedTypeHandler;
                }
                return result;
            }
            set { _oidIndex[oid] = value; }
        }

        internal TypeHandler this[NpgsqlDbType npgsqlDbType, Type enumType = null]
        {
            get
            {
                TypeHandler handler;
                if (_byNpgsqlDbType.TryGetValue(npgsqlDbType, out handler)) {
                    return handler;
                }

                if (npgsqlDbType == NpgsqlDbType.Enum)
                {
                    if (enumType == null) {
                        throw new InvalidCastException("Either specify EnumType along with NpgsqlDbType.Enum, or leave both empty to infer from Value");
                    }

                    if (!_byType.TryGetValue(enumType, out handler)) {
                        throw new NotSupportedException("This enum type is not supported (have you registered it in Npsql and set the EnumType property of NpgsqlParameter?)");
                    }
                    return handler;
                }

                if (npgsqlDbType == (NpgsqlDbType.Enum | NpgsqlDbType.Array))
                {
                    if (enumType == null) {
                        throw new InvalidCastException("Either specify EnumType along with NpgsqlDbType.Enum, or leave both empty to infer from Value");
                    }

                    if (_byEnumTypeAsArray != null && _byEnumTypeAsArray.TryGetValue(enumType, out handler)) {
                        return handler;
                    }
                    throw new NotSupportedException("This enum array type is not supported (have you registered it in Npsql and set the EnumType property of NpgsqlParameter?)");
                }

                throw new NotImplementedException("This NpgsqlDbType hasn't been implemented yet in Npgsql: " + npgsqlDbType);
            }
        }

        internal TypeHandler this[DbType dbType]
        {
            get
            {
                TypeHandler handler;
                if (!_byDbType.TryGetValue(dbType, out handler)) {
                    throw new NotSupportedException("This DbType is not supported in Npgsql: " + dbType);
                }
                return handler;
            }
        }

        internal TypeHandler this[object value]
        {
            get
            {
                Contract.Requires(value != null && !(value is DBNull));

                if (value is DateTime)
                {
                    return ((DateTime) value).Kind == DateTimeKind.Utc
                        ? this[NpgsqlDbType.TimestampTZ]
                        : this[NpgsqlDbType.Timestamp];
                }

                var type = value.GetType();

                TypeHandler handler;
                if (_byType.TryGetValue(type, out handler)) {
                    return handler;
                }

                if (type.IsArray)
                {
                    var elementType = type.GetElementType();
                    if (elementType.IsEnum) {
                        if (_byEnumTypeAsArray != null && _byEnumTypeAsArray.TryGetValue(elementType, out handler)) {
                            return handler;
                        }
                        throw new Exception("Enums must be registered with Npgsql via Connection.RegisterEnumType or RegisterEnumTypeGlobally");
                    }

                    if (!_byType.TryGetValue(elementType, out handler)) {
                        throw new NotSupportedException("This .NET type is not supported in Npgsql or your PostgreSQL: " + type);
                    }
                    return this[NpgsqlDbType.Array | handler.NpgsqlDbType];
                }

                if (value is IList)
                {
                    if (type.IsGenericType)
                    {
                        if (!_byType.TryGetValue(type.GetGenericArguments()[0], out handler)) {
                            throw new NotSupportedException("This .NET type is not supported in Npgsql or your PostgreSQL: " + type);
                        }
                        return this[NpgsqlDbType.Array | handler.NpgsqlDbType];
                    }
                    throw new NotSupportedException("Non-generic IList is a supported parameter, but the NpgsqlDbType parameter must be set on the parameter");
                }

                if (type.IsEnum) {
                    throw new Exception("Enums must be registered with Npgsql via Connection.RegisterEnumType or RegisterEnumTypeGlobally");
                }

                if (type.GetGenericTypeDefinition() == typeof(NpgsqlRange<>))
                {
                    if (!_byType.TryGetValue(type.GetGenericArguments()[0], out handler)) {
                        throw new NotSupportedException("This .NET range type is not supported in your PostgreSQL: " + type);
                    }
                    return this[NpgsqlDbType.Range | handler.NpgsqlDbType];
                }

                throw new NotSupportedException("This .NET type is not supported in Npgsql or your PostgreSQL: " + type);
            }
        }

        internal static NpgsqlDbType ToNpgsqlDbType(DbType dbType)
        {
            return DbTypeToNpgsqlDbType[dbType];
        }

        internal static NpgsqlDbType ToNpgsqlDbType(Type type)
        {
            NpgsqlDbType npgsqlDbType;
            if (TypeToNpgsqlDbType.TryGetValue(type, out npgsqlDbType)) {
                return npgsqlDbType;
            }

            if (type.IsArray)
            {
                if (type == typeof(byte[])) {
                    return NpgsqlDbType.Bytea;
                }
                return NpgsqlDbType.Array | ToNpgsqlDbType(type.GetElementType());
            }

            if (type.IsEnum) {
                return NpgsqlDbType.Enum;
            }

            if (type.GetGenericTypeDefinition() == typeof(NpgsqlRange<>)) {
                return NpgsqlDbType.Range | ToNpgsqlDbType(type.GetGenericArguments()[0]);
            }

            throw new NotSupportedException("Can't infer NpgsqlDbType for type " + type);
        }

        internal static DbType ToDbType(Type type)
        {
            DbType dbType;
            return TypeToDbType.TryGetValue(type, out dbType) ? dbType : DbType.Object;
        }

        internal static DbType ToDbType(NpgsqlDbType npgsqlDbType)
        {
            DbType dbType;
            return NpgsqlDbTypeToDbType.TryGetValue(npgsqlDbType, out dbType) ? dbType : DbType.Object;
        }

        #endregion

        #region Type Handler Discovery

        static TypeHandlerRegistry()
        {
            HandlerTypes = new Dictionary<string, TypeAndMapping>();
            NpgsqlDbTypeToDbType = new Dictionary<NpgsqlDbType, DbType>();
            DbTypeToNpgsqlDbType = new Dictionary<DbType, NpgsqlDbType>();
            TypeToNpgsqlDbType = new Dictionary<Type, NpgsqlDbType>();
            TypeToDbType = new Dictionary<Type, DbType>();

            foreach (var t in Assembly.GetExecutingAssembly().GetTypes().Where(t => t.IsSubclassOf(typeof(TypeHandler))))
            {
                var mappings = t.GetCustomAttributes(typeof(TypeMappingAttribute), false);
                if (!mappings.Any())
                    continue;

                foreach (TypeMappingAttribute m in mappings)
                {
                    if (HandlerTypes.ContainsKey(m.PgName)) {
                        throw new Exception("Two type handlers registered on same PostgreSQL type name: " + m.PgName);
                    }
                    HandlerTypes[m.PgName] = new TypeAndMapping { HandlerType=t, Mapping=m };
                    if (!m.NpgsqlDbType.HasValue) {
                        continue;
                    }
                    var npgsqlDbType = m.NpgsqlDbType.Value;

                    if (m.DbTypes.Any())
                    {
                        NpgsqlDbTypeToDbType[npgsqlDbType] = m.DbTypes.FirstOrDefault();
                    }
                    foreach (var dbType in m.DbTypes)
                    {
                        DbTypeToNpgsqlDbType[dbType] = npgsqlDbType;
                    }
                    foreach (var type in m.Types)
                    {
                        TypeToNpgsqlDbType[type] = npgsqlDbType;
                        TypeToDbType[type] = m.DbTypes.FirstOrDefault();
                    }
                }
            }
        }

        #endregion

        #region Misc

        static internal void ClearBackendTypeCache()
        {
            BackendTypeCache.Clear();
        }

        #endregion
    }

    class BackendType
    {
        internal string Name;
        internal uint OID;
        internal BackendTypeType Type;
        internal uint ArrayOID;
        internal uint RangeSubtypeOID;
    }

    struct TypeAndMapping
    {
        internal Type HandlerType;
        internal TypeMappingAttribute Mapping;
    }

    /// <summary>
    /// Specifies the type of a type, as represented in the PostgreSQL typtype column of the pg_type table.
    /// See http://www.postgresql.org/docs/9.4/static/catalog-pg-type.html
    /// </summary>
    enum BackendTypeType
    {
        Base = 'b',
        Range = 'r',
        Enum = 'e'
    }
}
