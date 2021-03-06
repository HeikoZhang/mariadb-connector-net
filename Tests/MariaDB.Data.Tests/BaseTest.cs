// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published
// by the Free Software Foundation; version 3 of the License.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License
// for more details.
//
// You should have received a copy of the GNU Lesser General Public License along
// with this program; if not, write to the Free Software Foundation, Inc.,
// 51 Franklin St, Fifth Floor, Boston, MA 02110-1301  USA

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace MariaDB.Data.MySqlClient.Tests
{
	/// <summary>
	/// Summary description for BaseTest.
	/// </summary>
	public class BaseTest
	{
		//statics
		protected static int maxPacketSize;

		protected static MySqlConnection rootConn;
		protected static string host;
		protected static string user;
		protected static string password;
		protected static int port;
		protected static string pipeName;
		protected static string memoryName;
		protected static string rootUser;
		protected static string rootPassword;
		protected static string database0;
		protected static string database1;
		protected static Version version;

		protected string table;
		protected string csAdditions = String.Empty;
		protected MySqlConnection conn;
		protected bool accessToMySqlDb;
		private int numProcessesRunning;

		public BaseTest()
		{
			if (host == null)
				LoadStaticConfiguration();
		}

		protected virtual void LoadStaticConfiguration()
		{
			Debug.Assert(host == null);

			user = "test";
			password = "test";
			port = 3306;
			rootUser = "root";
			rootPassword = "";
			string strPort = null;

			if (strPort != null)
				port = Int32.Parse(strPort);
			if (host == null)
				host = "localhost";
			if (pipeName == null)
				pipeName = "MYSQL";
			if (memoryName == null)
				memoryName = "MYSQL";

			// we don't use FileVersion because it's not available
			// on the compact framework
			if (database0 == null)
			{
				string fullname = typeof(BaseTest).GetTypeInfo().Assembly.FullName;
				string[] parts = fullname.Split(new char[] { '=' });
				string[] versionParts = parts[1].Split(new char[] { '.' });
				database0 = String.Format("db{0}{1}{2}-a", versionParts[0], versionParts[1], port - 3300);
				database1 = String.Format("db{0}{1}{2}-b", versionParts[0], versionParts[1], port - 3300);
			}

			string connStr = GetConnectionString(rootUser, rootPassword, false);
			rootConn = new MySqlConnection(connStr + ";database=mysql");
			rootConn.Open();

			if (rootConn.ServerVersion.StartsWith("5"))
			{
				// run all tests in strict mode
				MySqlCommand cmd = new MySqlCommand("SET GLOBAL SQL_MODE=STRICT_ALL_TABLES", rootConn);
				cmd.ExecuteNonQuery();
			}
		}

		protected Version Version
		{
			get
			{
				if (version == null)
				{
					string versionString = rootConn.ServerVersion;
					int i = 0;
					while (i < versionString.Length &&
						(Char.IsDigit(versionString[i]) || versionString[i] == '.'))
						i++;
					version = new Version(versionString.Substring(0, i));
				}
				return version;
			}
		}

		protected virtual string GetConnectionInfo()
		{
			return String.Format("protocol=sockets;port={0};", port);
		}

		protected string GetConnectionString(string userId, string pw, bool includedb)
		{
			Debug.Assert(userId != null);
			string connStr = String.Format("server={0};user id={1};pooling=false;" +
				 "persist security info=true;connection reset=true;allow user variables=true;",
				 host, userId);
			if (pw != null)
				connStr += String.Format(";password={0};", pw);
			if (includedb)
				connStr += String.Format("database={0};", database0);
			connStr += GetConnectionInfo();
			connStr += csAdditions;
			return connStr;
		}

		protected string GetConnectionString(bool includedb)
		{
			return GetConnectionString(user, password, includedb);
		}

		protected string GetPoolingConnectionString()
		{
			string s = GetConnectionString(true);
			s = s.Replace("pooling=false", "pooling=true");
			return s;
		}

		protected void Open()
		{
			string connString = GetConnectionString(true);
			conn = new MySqlConnection(connString);
			conn.Open();
		}

		protected void SetAccountPerms(bool includeProc)
		{
			// now allow our user to access them
			suExecSQL(String.Format(@"GRANT ALL ON `{0}`.* to 'test'@'localhost'
				identified by 'test'", database0));
			suExecSQL(String.Format(@"GRANT SELECT ON `{0}`.* to 'test'@'localhost'
				identified by 'test'", database1));
			if (Version.Major >= 5)
				suExecSQL(String.Format(@"GRANT EXECUTE ON `{0}`.* to 'test'@'localhost'
					identified by 'test'", database1));

			if (includeProc)
			{
				// now allow our user to access them
				suExecSQL(@"GRANT ALL ON mysql.proc to 'test'@'localhost' identified by 'test'");
			}

			suExecSQL("FLUSH PRIVILEGES");
		}

		public virtual void Setup()
		{
			Assembly executingAssembly = typeof(BaseTest).GetTypeInfo().Assembly;
			Stream stream = executingAssembly.GetManifestResourceStream("MySql.Data.MySqlClient.Tests.Properties.Setup.sql");
			StreamReader sr = new StreamReader(stream);
			string sql = sr.ReadToEnd();
			sr.Dispose();

			SetAccountPerms(accessToMySqlDb);
			sql = sql.Replace("[database0]", database0);
			sql = sql.Replace("[database1]", database1);

			ExecuteSQLAsRoot(sql);
			Open();
		}

		protected void ExecuteSQLAsRoot(string sql)
		{
			MySqlScript s = new MySqlScript(rootConn, sql);
			s.Execute();
		}

		public virtual void Teardown()
		{
			// wait up to 5 seconds for our connection to close
			if (Version.Major < 5)
				suExecSQL("REVOKE ALL PRIVILEGES, GRANT OPTION FROM 'test'");
			else
				suExecSQL("DROP USER 'test'@'localhost'");

			DropDatabase(database0);
			DropDatabase(database1);
		}

		private void DropDatabase(string name)
		{
			for (int i = 0; i < 5; i++)
			{
				try
				{
					suExecSQL(String.Format("DROP DATABASE IF EXISTS `{0}`", name));
					return;
				}
				catch (Exception)
				{
					System.Threading.Thread.Sleep(1000);
				}
			}            
		}

		protected void KillConnection(MySqlConnection c)
		{
			int threadId = c.ServerThread;
			MySqlCommand cmd = new MySqlCommand("KILL " + threadId, conn);
			cmd.ExecuteNonQuery();

			// the kill flag might need a little prodding to do its thing
			try
			{
				cmd.CommandText = "SELECT 1";
				cmd.Connection = c;
				cmd.ExecuteNonQuery();
			}
			catch (Exception) { }

			// now wait till the process dies
		}

		protected void KillPooledConnection(string connStr)
		{
			MySqlConnection c = new MySqlConnection(connStr);
			c.Open();
			KillConnection(c);
		}

		protected void createTable(string sql, string engine)
		{
			if (Version >= new Version(4, 1))
				sql += " ENGINE=" + engine;
			else
				sql += " TYPE=" + engine;
			execSQL(sql);
		}

		protected void suExecSQL(string sql)
		{
			Debug.Assert(rootConn != null);
			MySqlCommand cmd = new MySqlCommand(sql, rootConn);
			cmd.ExecuteNonQuery();
		}

		protected void execSQL(string sql)
		{
			MySqlCommand cmd = new MySqlCommand(sql, conn);
			cmd.ExecuteNonQuery();
		}

		protected bool TableExists(string tableName)
		{
			throw new NotImplementedException();
		}
	}
}