﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Text;

namespace innovea.Tools.SQL
{
    /**
     * 
     */
    public class DAOHelper
    {
        public enum SQLOperation
        {
            INSERT,
            UPDATE,
            DELETE,

            SELECT,
        }

        public class SQLInfo
        {
            public Type ClassType { get; }
            public string TableName { get; }
            public ISet<string> ColumnNames { get; }
            public IList<string> PKColumnNames { get; }
            public IDictionary<string, Func<object, object>> GettersByColumnName { get; }
            public IDictionary<string, Func<object, object, object>> SettersByColumnName { get; }
            public IDictionary<string, Type> ColumnTypesByColumnName { get; }

            public SQLInfo(Type classType)
            {
                ClassType = classType;
                TableName = GetTableName(classType);

                IDictionary<string, object[]> mappers = GetMappers(classType);

                ColumnNames = new HashSet<string>(mappers.Keys);
                PKColumnNames = mappers.Keys.Where(k => mappers[k][2] != null).OrderBy(k => mappers[k][2]).Select(k => k).ToList<string>();
                GettersByColumnName = ColumnNames.ToDictionary(k => k, k => (Func<object, object>) mappers[k][0]);
                SettersByColumnName = ColumnNames.ToDictionary(k => k, k => (Func<object, object, object>) mappers[k][1]);
                ColumnTypesByColumnName = ColumnNames.ToDictionary(k => k, k => (Type) mappers[k][3]);
            }
        }

        private DAOHelper() {}

        /**
         * This function is mainly to show an example of how to use ExecuteTx<T>(string connectionStr, Func<SqlTransaction, T> callback).
         * 
         */
        public static void ExecuteTx<T>(string connectionStr, SQLOperation sqlOperation, SQLInfo sqlInfo, IList<T> objects)
        {
            Type type = typeof(T);
            if (!type.Equals(sqlInfo.ClassType))
                throw new Exception("The SQLInfo class type should be the same than the T type");

            ExecuteTx<IList<T>>(connectionStr, Tx =>
            {
                string sql = FormatSQL(sqlOperation, sqlInfo);
                using (SqlCommand sqlCommand = new SqlCommand(sql, Tx.Connection, Tx))
                {
                    ExecuteBatch<T>(sqlCommand, sqlInfo, objects);
                }

                return objects;
            });
        }

        /**
         * This function is a template on how manage a Transaction in C#/ADO.NET.
         * 
         * The business code can be put in the callback function.
         * 
         */ 
        public static T ExecuteTx<T>(string connectionStr, Func<SqlTransaction, T> callback)
        {
            using (SqlConnection conn = new SqlConnection(connectionStr))
            {
                try
                {
                    conn.Open();
                    SqlTransaction tx = conn.BeginTransaction();
                    try
                    {
                        T result = callback(tx);

                        tx.Commit();

                        return result;
                    }
                    catch (Exception evt)
                    {
                        tx.Rollback();
                        throw evt;
                    }
                } finally {
                    conn.Close();
                }
            }
        }

        /**
         * This function does all the boilerplate code on how 
         * 
         */ 
        public static void ExecuteBatch<T>(SqlCommand sqlCommand, SQLInfo sqlInfo, IList<T> objects)
        {
            ISet<string> columnNames = sqlInfo.ColumnNames;
            IDictionary<string, Func<object, object>> getters = sqlInfo.GettersByColumnName;
            IDictionary<string, Type> columnTypesByColumnName = sqlInfo.ColumnTypesByColumnName;

            IDictionary<Type, SqlDbType> type2SqlDbTypeMapping = StandardType2SqlDbTypeMapping();

            foreach (object obj in objects)
            {
                foreach (string columName in columnNames)
                {
                    string cn = "@" + columName;
                    object value = getters[columName](obj);
                    SqlDbType sqlType = type2SqlDbTypeMapping[ columnTypesByColumnName[columName] ];

                    SqlParameter parameter = new SqlParameter();
                    parameter.ParameterName = cn;
                    parameter.SqlDbType = sqlType;

                    if (value != null)
                    {
                        parameter.Value = value;
                    }
                    else
                    {
                        parameter.Value = DBNull.Value;
                    }

                    sqlCommand.Parameters.Add(parameter);
                }

                sqlCommand.ExecuteNonQuery();
                sqlCommand.Parameters.Clear();
            }
        }

        public static IList<T> FindAll<T>(SqlTransaction Tx, IDictionary<string, Func<object, object, object>> settersByColumnName, string sql, IDictionary<string,object[]> conditions)
        {
            IDictionary<Type, SqlDbType> type2SqlDbTypeMapping = StandardType2SqlDbTypeMapping();

            using (SqlCommand sqlCommand = new SqlCommand(sql, Tx.Connection, Tx))
            {
                int index = 0;
                foreach(var condition in conditions)
                {
                    string columnName = condition.Key;
                    SqlDbType sqlType = (SqlDbType) condition.Value[0];
                    object value = condition.Value[1];

                    SqlParameter parameter = new SqlParameter();
                    parameter.ParameterName = columnName;
                    parameter.SqlDbType = sqlType;

                    if (value != null)
                    {
                        parameter.Value = value;
                    }
                    else
                    {
                        parameter.Value = DBNull.Value;
                    }

                    sqlCommand.Parameters.Add(parameter);

                    index++;
                }

                IList<T> objs = new List<T>();

                using (SqlDataReader reader = sqlCommand.ExecuteReader())
                {
                    try
                    {
                        while (reader.Read())
                        {
                            T obj = (T)typeof(T).GetConstructor(new Type[] { }).Invoke(new object[] { });

                            int nbColumn = reader.FieldCount;
                            for (int i = 0; i < nbColumn; i++)
                            {
                                string columnName = reader.GetName(i);

                                // Set only the field defined in the class
                                if (!settersByColumnName.ContainsKey(columnName))
                                    continue;

                                object value = reader.GetValue(i);

                                if (value != DBNull.Value)
                                    settersByColumnName[columnName](obj, value);
                                else
                                    settersByColumnName[columnName](obj, null);
                            }

                            objs.Add(obj);
                        }
                    }
                    catch(Exception evt)
                    {
                        throw evt;
                    }
                    finally
                    {
                        reader.Close();
                    }
                }

                return objs;
            }
        }

        public static T FindByPK<T>(SqlTransaction Tx, SQLInfo sqlInfo, object[] primaryKey)
        {
            IDictionary<Type, SqlDbType> type2SqlDbTypeMapping = StandardType2SqlDbTypeMapping();

            IDictionary<string, object[]> conditions = new Dictionary<string, object[]>();
            for(int i=0; i < sqlInfo.PKColumnNames.Count; i++)
            {
                string columnName = sqlInfo.PKColumnNames[i];
                Type type = sqlInfo.ColumnTypesByColumnName[columnName];
                SqlDbType sqlType = type2SqlDbTypeMapping[ type ];
                object value = primaryKey[i];

                conditions[columnName] = new object[] { sqlType, value };
            }

            return FindAll<T>(Tx, sqlInfo.SettersByColumnName, FormatSQL(SQLOperation.SELECT, sqlInfo), conditions).First<T>();
        }

        public static string FormatSQL(SQLOperation sqlOperation, SQLInfo sqlInfo)
        {
            StringBuilder builder = new StringBuilder();
            switch(sqlOperation)
            {
                case SQLOperation.INSERT:
                    builder.Append(sqlOperation.ToString()).Append(" INTO ").Append(sqlInfo.TableName);
                    builder.Append(" (").Append(String.Join(",", sqlInfo.ColumnNames)).Append(") ");
                    builder.Append(" VALUES ( @").Append(String.Join(", @", sqlInfo.ColumnNames)).Append(") ");
                    break;
                case SQLOperation.UPDATE:
                    if (sqlInfo.PKColumnNames.Count == 0)
                        throw new Exception("No PK defined - Cannot format the SQL/Update");

                    builder.Append(sqlOperation.ToString()).Append(" ").Append(sqlInfo.TableName).Append(" SET ");
                    builder.Append(String.Join(", ", sqlInfo.ColumnNames.Where(t => !sqlInfo.PKColumnNames.Contains(t)).ToList<string>().Select(t => t + " = @" + t)) );
                    builder.Append(sqlInfo.TableName).Append(" WHERE (");
                    builder.Append(String.Join(" AND ", sqlInfo.PKColumnNames.ToList<string>().Select(t => t + " = @" + t)));
                    builder.Append(")");
                    break;
                case SQLOperation.DELETE:
                    if (sqlInfo.PKColumnNames.Count == 0)
                        throw new Exception("No PK defined - Cannot format the SQL/Delete");

                    builder.Append(sqlOperation.ToString()).Append(" FROM ").Append(sqlInfo.TableName);
                    builder.Append(" WHERE (");
                    builder.Append(String.Join(" AND ", sqlInfo.PKColumnNames.ToList<string>().Select(t => t + " = @" + t)));
                    builder.Append(")");
                    break;

                case SQLOperation.SELECT:
                    builder.Append(sqlOperation.ToString()).Append(" * FROM ").Append(sqlInfo.TableName);

                    if (sqlInfo.PKColumnNames.Count == 0)
                        return builder.ToString();

                    builder.Append(" WHERE (");
                    builder.Append(String.Join(" AND ", sqlInfo.PKColumnNames.ToList<string>().Select(t => t + " = @" + t)));
                    builder.Append(")");
                    break;
                default:
                    throw new Exception("Does not know how to format this SQL command");
            }
            return builder.ToString();
        }

        public static string GetTableName(Type classType)
        {
            Table tableAttr = (Table) classType.GetCustomAttribute(typeof(Table));
            if (tableAttr == null)
                return null;

            return tableAttr.Name;
        }

        public static IDictionary<string, object[]> GetMappers(Type classType)
        {
            IDictionary<string, object[]> columnNames = new Dictionary<string, object[]>();
            classType.GetInterfaces().ToList().ForEach(i => GetMappers(columnNames, i));
            GetMappers(columnNames, classType);
            return columnNames;
        }

        private static void GetMappers(IDictionary<string, object[]> columnNames, Type classType)
        {
            foreach (var property in classType.GetProperties())
            {
                Column columnAttr = (Column)property.GetCustomAttribute(typeof(Column));
                if (columnAttr == null)
                    continue;

                string columnName = columnAttr.Name;
                int? pkOrder = columnAttr.PK;

                if (columnName == null)
                    columnName = property.Name;

                Func<object, object> getter;
                Type returnType;
                {
                    MethodInfo refGetter = property.GetMethod;
                    returnType = refGetter.ReturnType;
                    getter = o => refGetter.Invoke(o, null);
                }

                Func<object, object, object> setter;
                {
                    MethodInfo refSetter = property.SetMethod;
                    setter = (o, v) => refSetter.Invoke(o, new object[] { v });
                }

                columnNames.Add(columnName, new object[] { getter, setter, pkOrder, returnType });
            }
        }

        public static IDictionary<Type, SqlDbType> StandardType2SqlDbTypeMapping()
        {
            var typeMap = new Dictionary<Type, SqlDbType>();

            typeMap[typeof(string)] = SqlDbType.NVarChar;
            typeMap[typeof(char[])] = SqlDbType.NVarChar;
            typeMap[typeof(int)] = SqlDbType.Int;
            typeMap[typeof(Int32)] = SqlDbType.Int;
            typeMap[typeof(Int16)] = SqlDbType.SmallInt;
            typeMap[typeof(Int64)] = SqlDbType.BigInt;
            typeMap[typeof(Byte[])] = SqlDbType.VarBinary;
            typeMap[typeof(Boolean)] = SqlDbType.Bit;
            typeMap[typeof(DateTime)] = SqlDbType.DateTime2;
            typeMap[typeof(DateTimeOffset)] = SqlDbType.DateTimeOffset;
            typeMap[typeof(Decimal)] = SqlDbType.Decimal;
            typeMap[typeof(Double)] = SqlDbType.Float;
            typeMap[typeof(Decimal)] = SqlDbType.Money;
            typeMap[typeof(Byte)] = SqlDbType.TinyInt;
            typeMap[typeof(TimeSpan)] = SqlDbType.Time;

            return typeMap;
        }
    }
}
