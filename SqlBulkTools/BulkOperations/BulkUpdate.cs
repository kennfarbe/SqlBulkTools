﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using SqlBulkTools.Enumeration;

// ReSharper disable once CheckNamespace
namespace SqlBulkTools
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class BulkUpdate<T> : AbstractOperation<T>, ITransaction
    {
        private Dictionary<string, bool> _nullableColumnDic;

        /// <summary>
        /// Updates existing records in bulk. 
        /// </summary>
        /// <param name="list"></param>
        /// <param name="tableName"></param>
        /// <param name="schema"></param>
        /// <param name="columns"></param>
        /// <param name="customColumnMappings"></param>
        /// <param name="bulkCopySettings"></param>
        /// <param name="propertyInfoList"></param>
        public BulkUpdate(IEnumerable<T> list, string tableName, string schema, HashSet<string> columns,
            Dictionary<string, string> customColumnMappings, BulkCopySettings bulkCopySettings, List<PropertyInfo> propertyInfoList)
            :
            base(list, tableName, schema, columns, customColumnMappings, bulkCopySettings, propertyInfoList)
        {
            _updatePredicates = new List<PredicateCondition>();
            _parameters = new List<SqlParameter>();
            _conditionSortOrder = 1;
            _nullableColumnDic = new Dictionary<string, bool>();
        }

        /// <summary>
        /// Only update records when the target satisfies a speicific requirement. This is used in conjunction with MatchTargetOn.
        /// See help docs for examples.  
        /// </summary>
        /// <param name="predicate"></param>
        /// <returns></returns>
        /// <exception cref="SqlBulkToolsException"></exception>
        public BulkUpdate<T> UpdateWhen(Expression<Func<T, bool>> predicate)
        {
            BulkOperationsHelper.AddPredicate(predicate, PredicateType.Update, _updatePredicates, _parameters, _conditionSortOrder, Constants.UniqueParamIdentifier);
            _conditionSortOrder++;

            return this;
        }

        /// <summary>
        /// At least one MatchTargetOn is required for correct configuration. MatchTargetOn is the matching clause for evaluating 
        /// each row in table. This is usally set to the unique identifier in the table (e.g. Id). Multiple MatchTargetOn members are allowed 
        /// for matching composite relationships. 
        /// </summary>
        /// <param name="columnName"></param>
        /// <returns></returns>
        public BulkUpdate<T> MatchTargetOn(Expression<Func<T, object>> columnName)
        {
            var propertyName = BulkOperationsHelper.GetPropertyName(columnName);

            if (propertyName == null)
                throw new SqlBulkToolsException("MatchTargetOn column name can't be null.");

            _matchTargetOn.Add(propertyName);

            return this;
        }

        /// <summary>
        /// At least one MatchTargetOn is required for correct configuration. MatchTargetOn is the matching clause for evaluating 
        /// each row in table. This is usally set to the unique identifier in the table (e.g. Id). Multiple MatchTargetOn members are allowed 
        /// for matching composite relationships. 
        /// </summary>
        /// <param name="columnName"></param>
        /// <param name="collation">Only explicitly set the collation if there is a collation conflict.</param>
        /// <returns></returns>
        public BulkUpdate<T> MatchTargetOn(Expression<Func<T, object>> columnName, string collation)
        {
            var propertyName = BulkOperationsHelper.GetPropertyName(columnName);

            if (propertyName == null)
                throw new NullReferenceException("MatchTargetOn column name can't be null.");

            _matchTargetOn.Add(propertyName);
            base.SetCollation(propertyName, collation);

            return this;
        }

        /// <summary>
        /// Sets the identity column for the table. Required if an Identity column exists in table and one of the two 
        /// following conditions is met: (1) MatchTargetOn list contains an identity column (2) AddAllColumns is used in setup. 
        /// </summary>
        /// <param name="columnName"></param>
        /// <returns></returns>
        public BulkUpdate<T> SetIdentityColumn(Expression<Func<T, object>> columnName)
        {
            base.SetIdentity(columnName);
            return this;
        }

        /// <summary>
        /// Sets the identity column for the table. Required if an Identity column exists in table and one of the two 
        /// following conditions is met: (1) MatchTargetOn list contains an identity column (2) AddAllColumns is used in setup. 
        /// </summary>
        /// <param name="columnName"></param>
        /// <param name="outputIdentity"></param>
        /// <returns></returns>
        public BulkUpdate<T> SetIdentityColumn(Expression<Func<T, object>> columnName, ColumnDirectionType outputIdentity)
        {
            base.SetIdentity(columnName, outputIdentity);
            return this;
        }

        /// <summary>
        /// Sets the table hint to be used in the merge query. HOLDLOCk is the default that will be used if one is not set. 
        /// </summary>
        /// <param name="tableHint"></param>
        /// <returns></returns>
        public BulkUpdate<T> SetTableHint(string tableHint)
        {
            _tableHint = tableHint;
            return this;
        }

        /// <summary>
        /// Commits a transaction to database. A valid setup must exist for the operation to be 
        /// successful.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="commandTimeout"></param>
        /// <returns></returns>
        /// <exception cref="IdentityException"></exception>
        public int Commit(SqlConnection connection, int commandTimeout = 0)
        {
            try
            {
                int affectedRows = 0;
                if (!_list.Any())
                {
                    return affectedRows;
                }

                base.MatchTargetCheck();

                DataTable dt = BulkOperationsHelper.CreateDataTable<T>(_propertyInfoList, _columns, _customColumnMappings, _ordinalDic, _matchTargetOn, _outputIdentity);
                dt = BulkOperationsHelper.ConvertListToDataTable(_propertyInfoList, dt, _list, _columns, _ordinalDic, _outputIdentityDic);

                // Must be after ToDataTable is called. 
                BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _columns, _matchTargetOn);
                BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _updatePredicates);

                if (connection.State == ConnectionState.Closed)
                    connection.Open();

                BulkOperationsHelper.ValidateMsSqlVersion(connection, OperationType.Update);

                var dtCols = BulkOperationsHelper.GetDatabaseSchema(connection, _schema, _tableName);


                SqlCommand command = connection.CreateCommand();
                command.Connection = connection;
                command.CommandTimeout = commandTimeout;

                //Creating temp table on database
                var schemaDetail = BulkOperationsHelper.BuildCreateTempTable(_columns, dtCols, _outputIdentity);
                command.CommandText = schemaDetail.BuildCreateTableQuery;
                command.ExecuteNonQuery();

                _nullableColumnDic = schemaDetail.NullableDic;

                //Insert into temp table
                if (BulkOperationsHelper.GetBulkInsertStrategyType(dt, _columns) ==
                    BulkInsertStrategyType.MultiValueInsert)
                {

                    var tempTableSetup = BulkOperationsHelper.BuildInsertQueryFromDataTable(_customColumnMappings, dt, _identityColumn, _columns,
                        _bulkCopySettings, schemaDetail, tableName: Constants.TempTableName, keepIdentity: true, keepInternalId: true);
                    command.CommandText = tempTableSetup.InsertQuery;
                    command.Parameters.AddRange(tempTableSetup.SqlParameterList.ToArray());
                    command.ExecuteNonQuery();
                    command.Parameters.Clear();
                }
                else
                    BulkOperationsHelper.InsertToTmpTableWithBulkCopy(connection, dt, _bulkCopySettings);

                string comm = BulkOperationsHelper.GetOutputCreateTableCmd(_outputIdentity, Constants.TempOutputTableName,
                OperationType.Update, _identityColumn);

                if (!string.IsNullOrWhiteSpace(comm))
                {
                    command.CommandText = comm;
                    command.ExecuteNonQuery();
                }

                comm = GetCommand(connection);

                command.CommandText = comm;

                if (_parameters.Count > 0)
                {
                    command.Parameters.AddRange(_parameters.ToArray());
                }

                affectedRows = command.ExecuteNonQuery();

                if (_outputIdentity == ColumnDirectionType.InputOutput)
                {
                    BulkOperationsHelper.LoadFromTmpOutputTable(command, _identityColumn, _outputIdentityDic, OperationType.InsertOrUpdate, _list);
                }

                return affectedRows;
            }

            catch (SqlException e)
            {
                for (int i = 0; i < e.Errors.Count; i++)
                {
                    // Error 8102 is identity error. 
                    if (e.Errors[i].Number == 544 || e.Errors[i].Number == 8102)
                    {
                        // Expensive call but neccessary to inform user of an important configuration setup. 
                        throw new IdentityException(e.Errors[i].Message);
                    }
                }
                throw;
            }

        }

        /// <summary>
        /// Commits a transaction to database asynchronously. A valid setup must exist for the operation to be 
        /// successful.
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="commandTimeout"></param>
        /// <returns></returns>
        /// <exception cref="IdentityException"></exception>
        public async Task<int> CommitAsync(SqlConnection connection, int commandTimeout = 0)
        {
            try
            {
                int affectedRows = 0;
                if (!_list.Any())
                {
                    return affectedRows;
                }

                base.MatchTargetCheck();

                DataTable dt = BulkOperationsHelper.CreateDataTable<T>(_propertyInfoList, _columns, _customColumnMappings, _ordinalDic, _matchTargetOn, _outputIdentity);
                dt = BulkOperationsHelper.ConvertListToDataTable(_propertyInfoList, dt, _list, _columns, _ordinalDic, _outputIdentityDic);

                // Must be after ToDataTable is called. 
                BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _columns, _matchTargetOn);
                BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _updatePredicates);

                if (connection.State == ConnectionState.Closed)
                    await connection.OpenAsync();

                BulkOperationsHelper.ValidateMsSqlVersion(connection, OperationType.Update);

                var dtCols = BulkOperationsHelper.GetDatabaseSchema(connection, _schema, _tableName);


                SqlCommand command = connection.CreateCommand();
                command.Connection = connection;
                command.CommandTimeout = commandTimeout;

                //Creating temp table on database
                var schemaDetail = BulkOperationsHelper.BuildCreateTempTable(_columns, dtCols, _outputIdentity);
                command.CommandText = schemaDetail.BuildCreateTableQuery;
                await command.ExecuteNonQueryAsync();

                _nullableColumnDic = schemaDetail.NullableDic;

                //Insert into temp table
                if (BulkOperationsHelper.GetBulkInsertStrategyType(dt, _columns) ==
                    BulkInsertStrategyType.MultiValueInsert)
                {

                    var tempTableSetup = BulkOperationsHelper.BuildInsertQueryFromDataTable(_customColumnMappings, dt, _identityColumn, _columns,
                        _bulkCopySettings, schemaDetail, Constants.TempTableName, keepIdentity: true, keepInternalId: true);
                    command.CommandText = tempTableSetup.InsertQuery;
                    command.Parameters.AddRange(tempTableSetup.SqlParameterList.ToArray());
                    await command.ExecuteNonQueryAsync();
                    command.Parameters.Clear();
                }
                else
                    await BulkOperationsHelper.InsertToTmpTableWithBulkCopyAsync(connection, dt, _bulkCopySettings);

                string comm = BulkOperationsHelper.GetOutputCreateTableCmd(_outputIdentity, Constants.TempOutputTableName,
                    OperationType.Update, _identityColumn);

                if (!string.IsNullOrWhiteSpace(comm))
                {
                    command.CommandText = comm;
                    await command.ExecuteNonQueryAsync();
                }

                comm = GetCommand(connection);

                command.CommandText = comm;

                if (_parameters.Count > 0)
                {
                    command.Parameters.AddRange(_parameters.ToArray());
                }

                affectedRows = await command.ExecuteNonQueryAsync();

                if (_outputIdentity == ColumnDirectionType.InputOutput)
                {
                    await BulkOperationsHelper.LoadFromTmpOutputTableAsync(command, _identityColumn, _outputIdentityDic, OperationType.InsertOrUpdate, _list);
                }

                return affectedRows;
            }

            catch (SqlException e)
            {
                for (int i = 0; i < e.Errors.Count; i++)
                {
                    // Error 8102 is identity error. 
                    if (e.Errors[i].Number == 544 || e.Errors[i].Number == 8102)
                    {
                        // Expensive call but neccessary to inform user of an important configuration setup. 
                        throw new IdentityException(e.Errors[i].Message);
                    }
                }
                throw;
            }
        }

        private string GetCommand(SqlConnection connection)
        {
            string comm = "MERGE INTO " + BulkOperationsHelper.GetFullQualifyingTableName(connection.Database, _schema, _tableName) + $" WITH ({_tableHint}) AS Target " +
                              "USING " + Constants.TempTableName + " AS Source " +
                              BulkOperationsHelper.BuildJoinConditionsForInsertOrUpdate(_matchTargetOn.ToArray(),
                                  Constants.SourceAlias, Constants.TargetAlias, base._collationColumnDic, _nullableColumnDic) +
                              "WHEN MATCHED " + BulkOperationsHelper.BuildPredicateQuery(_matchTargetOn.ToArray(), _updatePredicates, Constants.TargetAlias, base._collationColumnDic) +
                              "THEN UPDATE " +
                              BulkOperationsHelper.BuildUpdateSet(_columns, Constants.SourceAlias, Constants.TargetAlias, _identityColumn) +
                              BulkOperationsHelper.GetOutputIdentityCmd(_identityColumn, _outputIdentity, Constants.TempOutputTableName,
                              OperationType.Update) + "; " +
                              "DROP TABLE " + Constants.TempTableName + ";";

            return comm;
        }
    }
}
