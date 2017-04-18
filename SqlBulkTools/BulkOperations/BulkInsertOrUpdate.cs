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
    public class BulkInsertOrUpdate<T> : AbstractOperation<T>, ITransaction
    {
        private bool _deleteWhenNotMatchedFlag;
        private bool _excludeAllColumnsFromUpdate;
        private readonly HashSet<string> _excludeFromUpdate;
        private Dictionary<string, bool> _nullableColumnDic;        

        /// <summary>
        /// 
        /// </summary>
        /// <param name="list"></param>
        /// <param name="tableName"></param>
        /// <param name="schema"></param>
        /// <param name="columns"></param>
        /// <param name="customColumnMappings"></param>
        /// <param name="bulkCopySettings"></param>
        /// <param name="propertyInfoList"></param>
        public BulkInsertOrUpdate(IEnumerable<T> list, string tableName, string schema, HashSet<string> columns,
            Dictionary<string, string> customColumnMappings, BulkCopySettings bulkCopySettings, List<PropertyInfo> propertyInfoList) :

            base(list, tableName, schema, columns, customColumnMappings, bulkCopySettings, propertyInfoList)
        {
            _deleteWhenNotMatchedFlag = false;
            _excludeAllColumnsFromUpdate = false;
            _updatePredicates = new List<PredicateCondition>();
            _deletePredicates = new List<PredicateCondition>();
            _parameters = new List<SqlParameter>();
            _conditionSortOrder = 1;
            _excludeFromUpdate = new HashSet<string>();
            _nullableColumnDic = new Dictionary<string, bool>();
        }

        /// <summary>
        /// At least one MatchTargetOn is required for correct configuration. MatchTargetOn is the matching clause for evaluating 
        /// each row in table. This is usally set to the unique identifier in the table (e.g. Id). Multiple MatchTargetOn members are allowed 
        /// for matching composite relationships. 
        /// </summary>
        /// <param name="columnName"></param>
        /// <returns></returns>
        public BulkInsertOrUpdate<T> MatchTargetOn(Expression<Func<T, object>> columnName)
        {
            var propertyName = BulkOperationsHelper.GetPropertyName(columnName);

            if (propertyName == null)
                throw new NullReferenceException("MatchTargetOn column name can't be null.");

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
        public BulkInsertOrUpdate<T> MatchTargetOn(Expression<Func<T, object>> columnName, string collation)
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
        public BulkInsertOrUpdate<T> SetIdentityColumn(Expression<Func<T, object>> columnName)
        {
            SetIdentity(columnName);
            return this;
        }

        /// <summary>
        /// Sets the identity column for the table. Required if an Identity column exists in table and one of the two 
        /// following conditions is met: (1) MatchTargetOn list contains an identity column (2) AddAllColumns is used in setup. 
        /// </summary>
        /// <param name="columnName"></param>
        /// <param name="outputIdentity"></param>
        /// <returns></returns>
        public BulkInsertOrUpdate<T> SetIdentityColumn(Expression<Func<T, object>> columnName, ColumnDirectionType outputIdentity)
        {
            base.SetIdentity(columnName, outputIdentity);
            return this;
        }

        /// <summary>
        /// Exclude a property from the update statement. Useful for when you want to include CreatedDate or Guid for inserts only. 
        /// </summary>
        /// <param name="columnName"></param>
        /// <returns></returns>
        public BulkInsertOrUpdate<T> ExcludeColumnFromUpdate(Expression<Func<T, object>> columnName)
        {
            var propertyName = BulkOperationsHelper.GetPropertyName(columnName);

            if (propertyName == null)
                throw new SqlBulkToolsException("ExcludeColumnFromUpdate column name can't be null");


            if (!_columns.Contains(propertyName))
            {
                throw new SqlBulkToolsException("ExcludeColumnFromUpdate could not exclude column from update because column could not " +
                                                "be recognised. Call AddAllColumns() or AddColumn() for this column first.");
            }
            _excludeFromUpdate.Add(propertyName);

            return this;
        }

        /// <summary>
        /// Excludes all columns from update. Only inserts will be considered. Use the ExcludeColumnFromUpdate method if you want to
        /// only exclude specific columns.
        /// </summary>
        /// <param name="excludeAllColumns"></param>
        /// <returns></returns>
        public BulkInsertOrUpdate<T> ExcludeAllColumnsFromUpdate(bool excludeAllColumns = true)
        {
            if (_excludeAllColumnsFromUpdate)
                throw new SqlBulkToolsException("ExcludeAllColumnsFromUpdate can only be called once");

            if (_updatePredicates.Count > 0)
                throw new SqlBulkToolsException("Can't combine UpdateWhen and ExcludeAllColumnsFromUpdate. " +
                    "UpdateWhen predicate will not be evaluated if all columns are being excluded.");

            _excludeAllColumnsFromUpdate = excludeAllColumns;

            return this;
        }

        /// <summary>
        /// Only delete records when the target satisfies a speicific requirement. This is used in conjunction with MatchTargetOn.
        /// See help docs for examples
        /// </summary>
        /// <param name="predicate"></param>
        /// <returns></returns>
        public BulkInsertOrUpdate<T> DeleteWhen(Expression<Func<T, bool>> predicate)
        {
            _deleteWhenNotMatchedFlag = true;
            BulkOperationsHelper.AddPredicate(predicate, PredicateType.Delete, _deletePredicates, _parameters, _conditionSortOrder, Constants.UniqueParamIdentifier);
            _conditionSortOrder++;

            return this;
        }

        /// <summary>
        /// Sets the table hint to be used in the merge query. HOLDLOCk is the default that will be used if one is not set. 
        /// </summary>
        /// <param name="tableHint"></param>
        /// <returns></returns>
        public BulkInsertOrUpdate<T> SetTableHint(string tableHint)
        {
            _tableHint = tableHint;
            return this;
        }

        /// <summary>
        /// Only update records when the target satisfies a speicific requirement. This is used in conjunction with MatchTargetOn.
        /// See help docs for examples.  
        /// </summary>
        /// <param name="predicate"></param>
        /// <returns></returns>
        /// <exception cref="SqlBulkToolsException"></exception>
        public BulkInsertOrUpdate<T> UpdateWhen(Expression<Func<T, bool>> predicate)
        {
            BulkOperationsHelper.AddPredicate(predicate, PredicateType.Update, _updatePredicates, _parameters, _conditionSortOrder, Constants.UniqueParamIdentifier);
            _conditionSortOrder++;

            return this;
        }

        /// <summary>
        /// If a target record can't be matched to a source record, it's deleted. Notes: (1) This is false by default. (2) Use at your own risk.
        /// </summary>
        /// <param name="flag"></param>
        /// <returns></returns>
        public BulkInsertOrUpdate<T> DeleteWhenNotMatched(bool flag)
        {
            _deleteWhenNotMatchedFlag = flag;
            return this;
        }

        /// <summary>
        /// Commits a transaction to database. A valid setup must exist for the operation to be 
        /// successful.
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        /// <exception cref="SqlBulkToolsException"></exception>
        /// <exception cref="IdentityException"></exception>
        public int Commit(SqlConnection connection)
        {            
            int affectedRows = 0;
            if (!_list.Any())
            {
                return affectedRows;
            }

            if (!_deleteWhenNotMatchedFlag && _deletePredicates.Count > 0)
                throw new SqlBulkToolsException($"{BulkOperationsHelper.GetPredicateMethodName(PredicateType.Delete)} only usable on BulkInsertOrUpdate " +
                                                $"method when 'DeleteWhenNotMatched' is set to true.");

            base.MatchTargetCheck();

            DataTable dt = BulkOperationsHelper.CreateDataTable<T>(_propertyInfoList, _columns, _customColumnMappings, _ordinalDic, _matchTargetOn, _outputIdentity);
            dt = BulkOperationsHelper.ConvertListToDataTable(_propertyInfoList, dt, _list, _columns, _ordinalDic, _outputIdentityDic);

            // Must be after ToDataTable is called. 
            BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _columns, _matchTargetOn);
            BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _deletePredicates);
            BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _updatePredicates);

            if (connection.State != ConnectionState.Open)
                connection.Open();

            BulkOperationsHelper.ValidateMsSqlVersion(connection, OperationType.InsertOrUpdate);

            var dtCols = BulkOperationsHelper.GetDatabaseSchema(connection, _schema, _tableName);

            try
            {
                SqlCommand command = connection.CreateCommand();

                command.Connection = connection;
                command.CommandTimeout = _sqlTimeout;

                //Creating temp table on database
                var schemaDetail = BulkOperationsHelper.BuildCreateTempTable(_columns, dtCols, _outputIdentity);
                command.CommandText = schemaDetail.BuildCreateTableQuery;

                command.ExecuteNonQuery();

                _nullableColumnDic = schemaDetail.NullableDic;

                if (BulkOperationsHelper.GetBulkInsertStrategyType(dt, _columns) ==
                    BulkInsertStrategyType.MultiValueInsert)
                {

                    var tempTableSetup = BulkOperationsHelper.BuildInsertQueryFromDataTable(dt, _identityColumn, _columns,
                        _ordinalDic, _bulkCopySettings, schemaDetail);
                    command.CommandText = tempTableSetup.InsertQuery;
                    command.Parameters.AddRange(tempTableSetup.SqlParameterList.ToArray());
                    command.ExecuteNonQuery();
                }
                else
                    BulkOperationsHelper.InsertToTmpTableWithBulkCopy(connection, dt, _bulkCopySettings);

                string comm = BulkOperationsHelper.GetOutputCreateTableCmd(_outputIdentity, Constants.TempOutputTableName,
                OperationType.InsertOrUpdate, _identityColumn);

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
                    if (e.Errors[i].Number == 8102)
                    {
                        // Expensive but neccessary to inform user of an important configuration setup. 
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
        /// <returns></returns>
        /// <exception cref="SqlBulkToolsException"></exception>
        /// <exception cref="IdentityException"></exception>
        public async Task<int> CommitAsync(SqlConnection connection)
        {          
            int affectedRows = 0;
            if (!_list.Any())
            {
                return affectedRows;
            }

            if (!_deleteWhenNotMatchedFlag && _deletePredicates.Count > 0)
                throw new SqlBulkToolsException($"{BulkOperationsHelper.GetPredicateMethodName(PredicateType.Delete)} only usable on BulkInsertOrUpdate " +
                                                $"method when 'DeleteWhenNotMatched' is set to true.");

            base.MatchTargetCheck();

            DataTable dt = BulkOperationsHelper.CreateDataTable<T>(_propertyInfoList, _columns, _customColumnMappings, _ordinalDic, _matchTargetOn, _outputIdentity);
            dt = BulkOperationsHelper.ConvertListToDataTable(_propertyInfoList, dt, _list, _columns, _ordinalDic, _outputIdentityDic);

            // Must be after ToDataTable is called. 
            BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _columns, _matchTargetOn);
            BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _deletePredicates);
            BulkOperationsHelper.DoColumnMappings(_customColumnMappings, _updatePredicates);

            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync();

            BulkOperationsHelper.ValidateMsSqlVersion(connection, OperationType.InsertOrUpdate);

            var dtCols = BulkOperationsHelper.GetDatabaseSchema(connection, _schema, _tableName);

            try
            {

                SqlCommand command = connection.CreateCommand();

                command.Connection = connection;
                command.CommandTimeout = _sqlTimeout;

                //Creating temp table on database
                var schemaDetail = BulkOperationsHelper.BuildCreateTempTable(_columns, dtCols, _outputIdentity);
                command.CommandText = schemaDetail.BuildCreateTableQuery;
                await command.ExecuteNonQueryAsync();

                _nullableColumnDic = schemaDetail.NullableDic;

                if (BulkOperationsHelper.GetBulkInsertStrategyType(dt, _columns) ==
                    BulkInsertStrategyType.MultiValueInsert)
                {

                    var tempTableSetup = BulkOperationsHelper.BuildInsertQueryFromDataTable(dt, _identityColumn, _columns,
                        _ordinalDic, _bulkCopySettings, schemaDetail);
                    command.CommandText = tempTableSetup.InsertQuery;
                    command.Parameters.AddRange(tempTableSetup.SqlParameterList.ToArray());
                    await command.ExecuteNonQueryAsync();
                }
                else
                    await BulkOperationsHelper.InsertToTmpTableWithBulkCopyAsync(connection, dt, _bulkCopySettings);

                string comm = BulkOperationsHelper.GetOutputCreateTableCmd(_outputIdentity, Constants.TempOutputTableName,
                OperationType.InsertOrUpdate, _identityColumn);

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
                    if (e.Errors[i].Number == 8102)
                    {
                        // Expensive but neccessary to inform user of an important configuration setup. 
                        throw new IdentityException(e.Errors[i].Message);
                    }
                }

                throw;
            }
        }

        private string GetCommand(SqlConnection connection)
        {
            string comm =
                    GetSetIdentityCmd(on: true) +
                    "MERGE INTO " + BulkOperationsHelper.GetFullQualifyingTableName(connection.Database, _schema, _tableName) +
                    $" WITH ({_tableHint}) AS Target " +
                    "USING " + Constants.TempTableName + " AS Source " +
                    BulkOperationsHelper.BuildJoinConditionsForInsertOrUpdate(_matchTargetOn.ToArray(),
                        Constants.SourceAlias, Constants.TargetAlias, base._collationColumnDic, _nullableColumnDic) +
                    GetMatchedTargetCmd() +
                    "WHEN NOT MATCHED BY TARGET THEN " +
                    BulkOperationsHelper.BuildMergeInsert(_columns, Constants.SourceAlias, _identityColumn, _bulkCopySettings) +
                    (_deleteWhenNotMatchedFlag ? " WHEN NOT MATCHED BY SOURCE " + BulkOperationsHelper.BuildPredicateQuery(_matchTargetOn.ToArray(),
                    _deletePredicates, Constants.TargetAlias, base._collationColumnDic) +
                    "THEN DELETE " : " ") +
                    BulkOperationsHelper.GetOutputIdentityCmd(_identityColumn, _outputIdentity, Constants.TempOutputTableName,
                        OperationType.InsertOrUpdate) + "; " +
                    GetSetIdentityCmd(on: false) +
                    "DROP TABLE " + Constants.TempTableName + ";";

            return comm;
        }

        private string GetMatchedTargetCmd()
        {
            // If user manually excludes every column, it's effectively the same as calling ExcludeAllColumnsFromUpdate() once.
            if (_excludeFromUpdate.Count == _columns.Count)
            {
                _excludeAllColumnsFromUpdate = true;
            }

            if (_excludeAllColumnsFromUpdate)
            {
                return string.Empty;
            }

            return "WHEN MATCHED " + BulkOperationsHelper.BuildPredicateQuery(_matchTargetOn.ToArray(), _updatePredicates, Constants.TargetAlias, base._collationColumnDic) +
                    "THEN UPDATE " +
                    BulkOperationsHelper.BuildUpdateSet(_columns, Constants.SourceAlias, Constants.TargetAlias, _identityColumn, _excludeFromUpdate, _bulkCopySettings);
        }

        private string GetSetIdentityCmd(bool on)
        {
            string onOrOffStr = on ? "ON" : "OFF";

            if (_bulkCopySettings == null)
                return string.Empty;

            return _bulkCopySettings.SqlBulkCopyOptions.HasFlag(SqlBulkCopyOptions.KeepIdentity) ? $"SET IDENTITY_INSERT [{_schema}].[{_tableName}] {onOrOffStr} " : string.Empty;
        }

    }
}
