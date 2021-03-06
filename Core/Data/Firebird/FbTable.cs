﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using FirebirdSql.Data.FirebirdClient;

namespace DataDevelop.Data.Firebird
{
	internal class FbTable : Table
	{
		private FbDatabase database;
		private bool isView;
		private string tableName;

		public FbTable(FbDatabase database)
			: base(database)
		{
			this.database = database;
		}

		public FbTable(FbDatabase database, bool isView)
			: base(database)
		{
			this.database = database;
			this.isView = isView;
		}

		public FbConnection Connection => database.Connection;

		public override bool IsView => isView;

		public string TableName
		{
			get
			{
				if (string.IsNullOrEmpty(tableName)) {
					return Name;
				}
				return tableName;
			}
			set { tableName = value; }
		}

        public string TableSchema { get; set; }

        public override string QuotedName
		{
			get
			{
				if (string.IsNullOrEmpty(TableSchema)) {
					return string.Concat('"', Name, '"');
				}
				return string.Concat('"', TableSchema, "\".\"", TableName, '"');
			}
		}

		public override string GetBaseSelectCommandText(TableFilter filter, bool excludeWhere)
		{
			var select = new StringBuilder();
			select.Append("SELECT ");
			filter.WriteColumnsProjection(select);
			select.Append(" FROM ");
			select.Append(QuotedName);
			if (filter != null && filter.IsRowFiltered && !excludeWhere) {
				select.Append(" WHERE ");
				filter.WriteWhereStatement(select);
			}
			return select.ToString();
		}

		public override bool Rename(string newName)
		{
			throw new InvalidOperationException("Table rename is not supported in Firebird.");
		}

		public override bool Delete()
		{
			using (var drop = Connection.CreateCommand()) {
				drop.CommandText = "DROP TABLE " + QuotedName;
				try {
					drop.ExecuteNonQuery();
					return true;
				} catch (FbException) {
					return false;
				}
			}
		}

		public override DataTable GetData(int startIndex, int count, TableFilter filter, TableSort sort)
		{
			var sql = new StringBuilder();
			sql.Append("SELECT ");

			filter.WriteColumnsProjection(sql);
			sql.Append(" FROM ");
			sql.Append(QuotedName);

			if (filter != null && filter.IsRowFiltered) {
				sql.Append(" WHERE ");
				filter.WriteWhereStatement(sql);
			}

			if (sort != null && sort.IsSorted) {
				sql.Append(" ORDER BY ");
				sort.WriteOrderBy(sql);
			}

			sql.Append($" ROWS {startIndex + 1} TO {startIndex + count}");

			var data = new DataTable(Name);
			using (var connectionScope = Database.CreateConnectionScope())
			using (var select = Connection.CreateCommand()) {
				select.CommandText = sql.ToString();
				using (var reader = select.ExecuteReader()) {
					data.Load(reader);
				}
			}
			return data;
		}

		public override string GenerateCreateStatement()
		{
			if (IsView) {
				try {
					Database.Connect();
					using (var command = Connection.CreateCommand()) {
						command.CommandText = "select rdb$view_source from rdb$relations where rdb$relation_name = @Name";
						command.Parameters.Add("Name", DbType.String).Value = Name;
						var select = command.ExecuteScalar().ToString();
						return "CREATE VIEW " + Name + " AS " + Environment.NewLine + select;
					}
				} finally {
					Database.Disconnect();
				}
			} else {
				return "-- Retrive CREATE Statement is only available to Views.";
			}
		}

		protected override void PopulateColumns(IList<Column> columnsCollection)
		{
			using (Database.CreateConnectionScope()) {
				var primaryKeys = new HashSet<string>();
				if (!IsReadOnly) {
					using (var command = Connection.CreateCommand()) {
						command.CommandText =
							"select " +
							"	TRIM(sg.rdb$field_name) as field_name " +
							"from " +
							"	rdb$indices ix " +
							"	left join rdb$index_segments sg on ix.rdb$index_name = sg.rdb$index_name " +
							"	left join rdb$relation_constraints rc on rc.rdb$index_name = ix.rdb$index_name " +
							"where " +
							"	rc.rdb$constraint_type = 'PRIMARY KEY' " +
							"	and rc.rdb$relation_name = @TableName";
						command.Parameters.AddWithValue("@TableName", Name.ToUpper());
						using (var reader = command.ExecuteReader()) {
							while (reader.Read()) {
								primaryKeys.Add(reader.GetString(0));
							}
						}
					}
				}
				using (var columns = database.Connection.GetSchema("Columns", new[] { null, TableSchema, TableName, null })) {
					foreach (DataRow row in columns.Rows) {
						var column = new Column(this) {
							Name = row["COLUMN_NAME"].ToString(),
							ProviderType = row["COLUMN_DATA_TYPE"].ToString().ToLower(),
						};
						column.Type = FbProvider.MapType(column.ProviderType);
						if (column.Type == typeof(string)) {
							column.ProviderType += "(" + row["COLUMN_SIZE"] + ")";
						}
						column.InPrimaryKey = primaryKeys.Contains(column.Name);
						columnsCollection.Add(column);
					}
				}
			}
		}

		protected override void PopulateTriggers(IList<Trigger> triggersCollection)
		{
			using (var triggers = database.Connection.GetSchema("Triggers", new[] { null, TableSchema, TableName, null })) {
				foreach (DataRow row in triggers.Rows) {
					if ((short)row["IS_SYSTEM_TRIGGER"] == 0) {
						triggersCollection.Add(new FbTrigger(this, (string)row["TRIGGER_NAME"]));
					}
				}
			}
		}

		protected override void PopulateForeignKeys(IList<ForeignKey> foreignKeysCollection)
		{
			using (var foreignKeys = database.Connection.GetSchema("ForeignKeys", new[] { null, TableSchema, TableName, null }))
			using (var foreignKeyColumns = database.Connection.GetSchema("ForeignKeyColumns", new[] { null, TableSchema, TableName, null })) {
				foreach (DataRow row in foreignKeys.Rows) {
					var fk = new ForeignKey((string)row["CONSTRAINT_NAME"], this) {
						PrimaryTable = (string)row["REFERENCED_TABLE_NAME"],
						ChildTable = (string)row["TABLE_NAME"]
					};
					foreach (DataRow fkColumn in foreignKeyColumns.Rows) {
						fk.Columns.Add(new ColumnsPair((string)fkColumn["REFERENCED_COLUMN_NAME"], (string)fkColumn["COLUMN_NAME"]));
					}
					foreignKeysCollection.Add(fk);
				}
			}
		}
	}
}
