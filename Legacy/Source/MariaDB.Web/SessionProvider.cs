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
using System.Collections.Specialized;
using System.Configuration;
using System.Configuration.Provider;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;
using System.Web;
using System.Web.Configuration;
using System.Web.Hosting;
using System.Web.SessionState;
using MariaDB.Data.MySqlClient;
using MariaDB.Web.Common;
using MariaDB.Web.General;

namespace MariaDB.Web.SessionState
{
    /// <summary>
    /// This class allows ASP.NET applications to store and manage session state information in a
    /// MySQL database.
    /// Expired session data is periodically deleted from the database.
    /// </summary>
    public class MySqlSessionStateStore : SessionStateStoreProviderBase
    {
        private string connectionString;
        private ConnectionStringSettings connectionStringSettings;
        private string eventSource = "MySQLSessionStateStore";
        private string eventLog = "Application";
        private string exceptionMessage = "An exception occurred. Please check the event log.";
        private Application app;

        private SessionStateSection sessionStateConfig;

        // cleanup  old session
        private Timer cleanupTimer;

        private int cleanupInterval;
        private bool cleanupRunning;

        private bool writeExceptionsToEventLog = false;

        /// <summary>
        /// Indicates whether to write exceptions to event log
        /// </summary>
        public bool WriteExceptionsToEventLog
        {
            get { return writeExceptionsToEventLog; }
            set { writeExceptionsToEventLog = value; }
        }

        public string ApplicationName
        {
            get { return app.Name; }
            set { app.Name = value; }
        }

        private int ApplicationId
        {
            get { return app.Id; }
        }

        /// <summary>
        /// Handles MySql exception.
        /// If WriteExceptionsToEventLog is set, will write exception info
        /// to event log.
        /// It throws provider exception (original exception is stored as inner exception)
        /// </summary>
        /// <param name="e">exception</param>
        /// <param name="action"> name of the function that throwed the exception</param>
        private void HandleMySqlException(MySqlException e, string action)
        {
            if (WriteExceptionsToEventLog)
            {
                using (EventLog log = new EventLog())
                {
                    log.Source = eventSource;
                    log.Log = eventLog;

                    string message = "An exception occurred communicating with the data source.\n\n";
                    message += "Action: " + action;
                    message += "Exception: " + e.ToString();
                    log.WriteEntry(message);
                }
            }
            throw new ProviderException(exceptionMessage, e);
        }

        /// <summary>
        /// Initializes the provider with the property values specified in the ASP.NET application configuration file
        /// </summary>
        /// <param name="name">The name of the provider instance to initialize.</param>
        /// <param name="config">Object that contains the names and values of configuration options for the provider.
        /// </param>
        public override void Initialize(string name, NameValueCollection config)
        {
            //Initialize values from web.config.
            if (config == null)
                throw new ArgumentException("config");
            if (name == null || name.Length == 0)
                throw new ArgumentException("name");
            if (String.IsNullOrEmpty(config["description"]))
            {
                config.Remove("description");
                config["description"] = "MySQL Session State Store Provider";
            }
            base.Initialize(name, config);
            string applicationName = HostingEnvironment.ApplicationVirtualPath;
            if (!String.IsNullOrEmpty(config["applicationName"]))
                applicationName = config["applicationName"];

            // Get <sessionState> configuration element.
            Configuration webConfig = WebConfigurationManager.OpenWebConfiguration(HostingEnvironment.ApplicationVirtualPath);
            sessionStateConfig = (SessionStateSection)webConfig.SectionGroups["system.web"].Sections["sessionState"];

            // Initialize connection.
            connectionStringSettings = ConfigurationManager.ConnectionStrings[config["connectionStringName"]];
            if (connectionStringSettings == null || connectionStringSettings.ConnectionString.Trim() == "")
                throw new HttpException("Connection string can not be blank");
            connectionString = connectionStringSettings.ConnectionString;

            writeExceptionsToEventLog = false;
            if (config["writeExceptionsToEventLog"] != null)
            {
                writeExceptionsToEventLog = (config["writeExceptionsToEventLog"].ToUpper() == "TRUE");
            }

            // Make sure we have the correct schema.
            SchemaManager.CheckSchema(connectionString, config);
            app = new Application(applicationName, base.Description);

            // Get the application id.
            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    conn.Open();
                    app.EnsureId(conn);
                    CheckStorageEngine(conn);
                    cleanupInterval = GetCleanupInterval(conn);
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "Initialize");
            }

            // Setup the cleanup timer
            if (cleanupInterval <= 0)
                cleanupInterval = 1;
            cleanupTimer = new Timer(new TimerCallback(CleanupOldSessions), null, 0,
                cleanupInterval * 1000 * 60);
        }

        /// <summary>
        /// This method creates a new SessionStateStoreData object for the current request.
        /// </summary>
        /// <param name="context">
        /// The HttpContext object for the current request.
        /// </param>
        /// <param name="timeout">
        /// The timeout value (in minutes) for the SessionStateStoreData object that is created.
        /// </param>
        public override SessionStateStoreData CreateNewStoreData(System.Web.HttpContext context, int timeout)
        {
            return new SessionStateStoreData(new SessionStateItemCollection(),
                SessionStateUtility.GetSessionStaticObjects(context), timeout);
        }

        /// <summary>
        /// This method adds a new session state item to the database.
        /// </summary>
        /// <param name="context">
        /// The HttpContext object for the current request.
        /// </param>
        /// <param name="id">
        /// The session ID for the current request.
        /// </param>
        /// <param name="timeout">
        /// The timeout value for the current request.
        /// </param>
        public override void CreateUninitializedItem(System.Web.HttpContext context, string id, int timeout)
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    MySqlCommand cmd = new MySqlCommand(
                       "INSERT INTO my_aspnet_Sessions" +
                       " (SessionId, ApplicationId, Created, Expires, LockDate," +
                       " LockId, Timeout, Locked, SessionItems, Flags)" +
                       " Values (@SessionId, @ApplicationId, NOW(), NOW() + INTERVAL @Timeout MINUTE, NOW()," +
                       " @LockId , @Timeout, @Locked, @SessionItems, @Flags)",
                       conn);

                    cmd.Parameters.AddWithValue("@SessionId", id);
                    cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);
                    cmd.Parameters.AddWithValue("@LockId", 0);
                    cmd.Parameters.AddWithValue("@Timeout", timeout);
                    cmd.Parameters.AddWithValue("@Locked", 0);
                    cmd.Parameters.AddWithValue("@SessionItems", null);
                    cmd.Parameters.AddWithValue("@Flags", 1);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "CreateUninitializedItem");
            }
        }

        /// <summary>
        /// This method releases all the resources for this instance.
        /// </summary>
        public override void Dispose()
        {
            if (cleanupTimer != null)
                cleanupTimer.Dispose();
        }

        /// <summary>
        /// This method allows the MySqlSessionStateStore object to perform any cleanup that may be
        /// required for the current request.
        /// </summary>
        /// <param name="context">The HttpContext object for the current request</param>
        public override void EndRequest(System.Web.HttpContext context)
        {
        }

        /// <summary>
        /// This method returns a read-only session item from the database.
        /// </summary>
        public override SessionStateStoreData GetItem(System.Web.HttpContext context, string id, out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            return GetSessionStoreItem(false, context, id, out locked, out lockAge, out lockId, out actions);
        }

        /// <summary>
        /// This method locks a session item and returns it from the database
        /// </summary>
        /// <param name="context">The HttpContext object for the current request</param>
        /// <param name="id">The session ID for the current request</param>
        /// <param name="locked">
        /// true if the session item is locked in the database; otherwise, it is false.
        /// </param>
        /// <param name="lockAge">
        /// TimeSpan object that indicates the amount of time the session item has been locked in the database.
        /// </param>
        /// <param name="lockId">
        /// A lock identifier object.
        /// </param>
        /// <param name="actions">
        /// A SessionStateActions enumeration value that indicates whether or
        /// not the session is uninitialized and cookieless.
        /// </param>
        /// <returns></returns>
        public override SessionStateStoreData GetItemExclusive(System.Web.HttpContext context, string id,
            out bool locked, out TimeSpan lockAge, out object lockId, out SessionStateActions actions)
        {
            return GetSessionStoreItem(true, context, id, out locked, out lockAge, out lockId, out actions);
        }

        /// <summary>
        ///  This method performs any per-request initializations that the MySqlSessionStateStore provider requires.
        /// </summary>
        public override void InitializeRequest(System.Web.HttpContext context)
        {
        }

        /// <summary>
        /// This method forcibly releases the lock on a session item in the database, if multiple attempts to
        /// retrieve the session item fail.
        /// </summary>
        /// <param name="context">The HttpContext object for the current request.</param>
        /// <param name="id">The session ID for the current request.</param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        public override void ReleaseItemExclusive(System.Web.HttpContext context, string id, object lockId)
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    MySqlCommand cmd = new MySqlCommand(
                        "UPDATE my_aspnet_Sessions SET Locked = 0, Expires = NOW() + INTERVAL @Timeout MINUTE " +
                        "WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId AND LockId = @LockId",
                        conn);

                    cmd.Parameters.AddWithValue("@Timeout", sessionStateConfig.Timeout.TotalMinutes);
                    cmd.Parameters.AddWithValue("@SessionId", id);
                    cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);
                    cmd.Parameters.AddWithValue("@LockId", lockId);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "ReleaseItemExclusive");
            }
        }

        /// <summary>
        /// This method removes the specified session item from the database
        /// </summary>
        /// <param name="context">The HttpContext object for the current request</param>
        /// <param name="id">The session ID for the current request</param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        /// <param name="item">The session item to remove from the database.</param>
        public override void RemoveItem(System.Web.HttpContext context, string id, object lockId, SessionStateStoreData item)
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    MySqlCommand cmd = new MySqlCommand("DELETE FROM my_aspnet_Sessions " +
                        " WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId AND LockId = @LockId",
                        conn);

                    cmd.Parameters.AddWithValue("@SessionId", id);
                    cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);
                    cmd.Parameters.AddWithValue("@LockId", lockId);

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "RemoveItem");
            }
        }

        /// <summary>
        /// This method resets the expiration date and timeout for a session item in the database.
        /// </summary>
        /// <param name="context">The HttpContext object for the current request</param>
        /// <param name="id">The session ID for the current request</param>
        public override void ResetItemTimeout(System.Web.HttpContext context, string id)
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    MySqlCommand cmd = new MySqlCommand(
                        "UPDATE my_aspnet_Sessions SET Expires = NOW() + INTERVAL @Timeout MINUTE" +
                       " WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId", conn);

                    cmd.Parameters.AddWithValue("@Timeout", sessionStateConfig.Timeout.TotalMinutes);
                    cmd.Parameters.AddWithValue("@SessionId", id);
                    cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);
                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "ResetItemTimeout");
            }
        }

        /// <summary>
        /// This method updates the session time information in the database with the specified session item,
        /// and releases the lock.
        /// </summary>
        /// <param name="context">The HttpContext object for the current request</param>
        /// <param name="id">The session ID for the current request</param>
        /// <param name="item">The session item containing new values to update the session item in the database with.
        /// </param>
        /// <param name="lockId">The lock identifier for the current request.</param>
        /// <param name="newItem">A Boolean value that indicates whether or not the session item is new in the database.
        /// A false value indicates an existing item.
        /// </param>
        public override void SetAndReleaseItemExclusive(System.Web.HttpContext context, string id, SessionStateStoreData item, object lockId, bool newItem)
        {
            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    // Serialize the SessionStateItemCollection as a byte array
                    byte[] sessItems = Serialize((SessionStateItemCollection)item.Items);
                    MySqlCommand cmd;
                    if (newItem)
                    {
                        //Insert the new session item . If there was expired session
                        //with the same SessionId and Application id, it will be removed

                        cmd = new MySqlCommand(
                            "REPLACE INTO my_aspnet_Sessions (SessionId, ApplicationId, Created, Expires," +
                            " LockDate, LockId, Timeout, Locked, SessionItems, Flags)" +
                            " Values(@SessionId, @ApplicationId, NOW(), NOW() + INTERVAL @Timeout MINUTE, NOW()," +
                            " @LockId , @Timeout, @Locked, @SessionItems, @Flags)", conn);
                        cmd.Parameters.AddWithValue("@SessionId", id);
                        cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);
                        cmd.Parameters.AddWithValue("@Timeout", item.Timeout);
                        cmd.Parameters.AddWithValue("@LockId", 0);
                        cmd.Parameters.AddWithValue("@Locked", 0);
                        cmd.Parameters.AddWithValue("@SessionItems", sessItems);
                        cmd.Parameters.AddWithValue("@Flags", 0);
                    }
                    else
                    {
                        //Update the existing session item.
                        cmd = new MySqlCommand(
                             "UPDATE my_aspnet_Sessions SET Expires = NOW() + INTERVAL @Timeout MINUTE," +
                             " SessionItems = @SessionItems, Locked = @Locked " +
                             " WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId AND LockId = @LockId",
                             conn);

                        cmd.Parameters.AddWithValue("@Timeout", item.Timeout);
                        cmd.Parameters.AddWithValue("@SessionItems", sessItems);
                        cmd.Parameters.AddWithValue("@Locked", 0);
                        cmd.Parameters.AddWithValue("@SessionId", id);
                        cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);
                        cmd.Parameters.AddWithValue("@LockId", lockId);
                    }

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "SetAndReleaseItemExclusive");
            }
        }

        /// <summary>
        ///  GetSessionStoreItem is called by both the GetItem and  GetItemExclusive methods. GetSessionStoreItem
        ///  retrieves the session data from the data source. If the lockRecord parameter is true (in the case of
        ///  GetItemExclusive), then GetSessionStoreItem  locks the record and sets a New LockId and LockDate.
        /// </summary>
        private SessionStateStoreData GetSessionStoreItem(bool lockRecord,
               HttpContext context,
               string id,
               out bool locked,
               out TimeSpan lockAge,
               out object lockId,
               out SessionStateActions actionFlags)
        {
            // Initial values for return value and out parameters.
            SessionStateStoreData item = null;
            lockAge = TimeSpan.Zero;
            lockId = null;
            locked = false;
            actionFlags = SessionStateActions.None;

            // MySqlCommand for database commands.
            MySqlCommand cmd = null;
            // serialized SessionStateItemCollection.
            byte[] serializedItems = null;
            // True if a record is found in the database.
            bool foundRecord = false;
            // True if the returned session item is expired and needs to be deleted.
            bool deleteData = false;
            // Timeout value from the data store.
            int timeout = 0;

            try
            {
                using (MySqlConnection conn = new MySqlConnection(connectionString))
                {
                    conn.Open();

                    // lockRecord is True when called from GetItemExclusive and
                    // False when called from GetItem.
                    // Obtain a lock if possible. Ignore the record if it is expired.
                    if (lockRecord)
                    {
                        cmd = new MySqlCommand(
                          "UPDATE my_aspnet_Sessions SET " +
                          " Locked = 1, LockDate = NOW()" +
                          " WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId AND" +
                          " Locked = 0 AND Expires > NOW()", conn);

                        cmd.Parameters.AddWithValue("@SessionId", id);
                        cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);

                        if (cmd.ExecuteNonQuery() == 0)
                        {
                            // No record was updated because the record was locked or not found.
                            locked = true;
                        }
                        else
                        {
                            // The record was updated.
                            locked = false;
                        }
                    }

                    // Retrieve the current session item information.
                    cmd = new MySqlCommand(
                      "SELECT NOW(), Expires , SessionItems, LockId,  Flags, Timeout, " +
                      "  LockDate " +
                      "  FROM my_aspnet_Sessions" +
                      "  WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId", conn);

                    cmd.Parameters.AddWithValue("@SessionId", id);
                    cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);

                    // Retrieve session item data from the data source.
                    using (MySqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            DateTime now = reader.GetDateTime(0);
                            DateTime expires = reader.GetDateTime(1);
                            if (now.CompareTo(expires) > 0)
                            {
                                //The record was expired. Mark it as not locked.
                                locked = false;
                                // The session was expired. Mark the data for deletion.
                                deleteData = true;
                            }
                            else
                            {
                                foundRecord = true;
                            }

                            object items = reader.GetValue(2);
                            serializedItems = (items is DBNull) ? null : (byte[])items;
                            lockId = reader.GetValue(3);
                            if (lockId is DBNull)
                                lockId = (int)0;

                            actionFlags = (SessionStateActions)(reader.GetInt32(4));
                            timeout = reader.GetInt32(5);
                            DateTime lockDate = reader.GetDateTime(6);
                            lockAge = now.Subtract(lockDate);
                        }
                    }

                    //If the returned session item is expired,
                    // delete the record from the data source.
                    if (deleteData)
                    {
                        cmd = new MySqlCommand("DELETE FROM my_aspnet_Sessions" +
                        " WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId", conn);
                        cmd.Parameters.AddWithValue("@SessionId", id);
                        cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);
                        cmd.ExecuteNonQuery();
                    }

                    // The record was not found. Ensure that locked is false.
                    if (!foundRecord)
                        locked = false;

                    // If the record was found and you obtained a lock, then set
                    // the lockId, clear the actionFlags,
                    // and create the SessionStateStoreItem to return.
                    if (foundRecord && !locked)
                    {
                        lockId = (int)(lockId) + 1;

                        cmd = new MySqlCommand("UPDATE my_aspnet_Sessions SET" +
                          " LockId = @LockId, Flags = 0 " +
                          " WHERE SessionId = @SessionId AND ApplicationId = @ApplicationId", conn);

                        cmd.Parameters.AddWithValue("@LockId", lockId);
                        cmd.Parameters.AddWithValue("@SessionId", id);
                        cmd.Parameters.AddWithValue("@ApplicationId", ApplicationId);

                        cmd.ExecuteNonQuery();

                        // If the actionFlags parameter is not InitializeItem,
                        // deserialize the stored SessionStateItemCollection.
                        if (actionFlags == SessionStateActions.InitializeItem)
                        {
                            item = CreateNewStoreData(context, (int)sessionStateConfig.Timeout.TotalMinutes);
                        }
                        else
                        {
                            item = Deserialize(context, serializedItems, timeout);
                        }
                    }
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "GetSessionStoreItem");
            }
            return item;
        }

        /// <summary>
        /// This method returns a false value to indicate that callbacks for expired sessions are not supported.
        /// </summary>
        /// <param name="expireCallback"></param>
        /// <returns>false </returns>
        public override bool SetItemExpireCallback(SessionStateItemExpireCallback expireCallback)
        {
            return false;
        }

        ///<summary>
        /// Serialize is called by the SetAndReleaseItemExclusive method to
        /// convert the SessionStateItemCollection into a byte array to
        /// be stored in the blob field.
        /// </summary>
        private byte[] Serialize(SessionStateItemCollection items)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(ms);
            if (items != null)
            {
                items.Serialize(writer);
            }
            writer.Close();
            return ms.ToArray();
        }

        ///<summary>
        /// Deserialize is called by the GetSessionStoreItem method to
        /// convert the byte array stored in the blob field to a
        /// SessionStateItemCollection.
        /// </summary>
        private SessionStateStoreData Deserialize(HttpContext context,
          byte[] serializedItems, int timeout)
        {
            SessionStateItemCollection sessionItems = new SessionStateItemCollection();

            if (serializedItems != null)
            {
                MemoryStream ms = new MemoryStream(serializedItems);
                if (ms.Length > 0)
                {
                    BinaryReader reader = new BinaryReader(ms);
                    sessionItems = SessionStateItemCollection.Deserialize(reader);
                }
            }

            return new SessionStateStoreData(sessionItems, SessionStateUtility.GetSessionStaticObjects(context),
                timeout);
        }

        private void CleanupOldSessions(object o)
        {
            if (cleanupRunning)
                return;

            cleanupRunning = true;
            try
            {
                using (MySqlConnection con = new MySqlConnection(connectionString))
                {
                    con.Open();
                    MySqlCommand cmd = new MySqlCommand(
                        "UPDATE my_aspnet_SessionCleanup SET LastRun=NOW() where" +
                        " LastRun + INTERVAL IntervalMinutes MINUTE < NOW()", con);

                    if (cmd.ExecuteNonQuery() > 0)
                    {
                        cmd = new MySqlCommand(
                           "DELETE FROM my_aspnet_Sessions WHERE Expires < NOW()",
                           con);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            catch (MySqlException e)
            {
                HandleMySqlException(e, "CleanupOldSessions");
            }
            finally
            {
                cleanupRunning = false;
            }
        }

        private int GetCleanupInterval(MySqlConnection con)
        {
            MySqlCommand cmd = new MySqlCommand("SELECT IntervalMinutes from my_aspnet_SessionCleanup", con);
            return (int)cmd.ExecuteScalar();
        }

        /// <summary>
        /// Check storage engine used by my_aspnet_Sessions.
        /// Warn if MyISAM is used - it does not handle concurrent updates well
        /// which is important for session provider, as each access to session
        /// does an update to "expires" field.
        /// </summary>
        /// <param name="con"></param>
        private void CheckStorageEngine(MySqlConnection con)
        {
            try
            {
                MySqlCommand cmd = new MySqlCommand(
                    "SELECT ENGINE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='my_aspnet_Sessions'",
                    con);
                using (MySqlDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        string engine = reader.GetString(0);
                        if (engine == "MyISAM")
                        {
                            string message =
                                "Storage engine for table my_aspnet_Sessions is MyISAM." +
                                "If possible, please change it to a transactional storage engine " +
                                 "to improve performance,e.g with 'alter table my_aspnet_Sessions engine innodb'\n";
                            try
                            {
                                using (EventLog log = new EventLog())
                                {
                                    log.Source = eventSource;
                                    log.Log = eventLog;
                                    log.WriteEntry(message);
                                }
                            }
                            catch (SecurityException)
                            {
                                // Can't write to event log due to security restrictions
                                Trace.WriteLine(message);
                            }
                        }
                    }
                }
            }
            catch (MySqlException e)
            {
                Trace.Write("got exception while checking for engine" + e);
            }
        }
    }
}