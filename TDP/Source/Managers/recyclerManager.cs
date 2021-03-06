﻿using System;
using System.Data;
using System.Collections;
using System.Threading;
using Ion.Storage;

namespace Holo.Managers
{
    /// <summary>
    /// Provides functions and tasks for the item Recycler, which allows users to trade in their items for special items.
    /// </summary>
    public static class recyclerManager
    {
        private static int sessionLength;
        private static int sessionExpireLength;
        private static int itemMinOwnershipLength;
        private static Hashtable sessionRewards;
        public static string setupString;
        /// <summary>
        /// Initializes the item Recycler, determining the state and creating the setup string.
        /// </summary>
        public static void Init(bool Update)
        {
            if (Config.getTableEntry("recycler_enable") == "1")
            {
                Config.enableRecycler = true;
                Out.WriteLine("Initializing recycler...");

                DataTable dTable;
                using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
                {
                    dTable = dbClient.getTable("SELECT rclr_cost, rclr_reward FROM system_recycler");
                }
                sessionLength = int.Parse(Config.getTableEntry("recycler_session_length"));
                sessionExpireLength = int.Parse(Config.getTableEntry("recycler_session_expirelength"));
                itemMinOwnershipLength = int.Parse(Config.getTableEntry("recycler_minownertime"));
                sessionRewards = new Hashtable();

                setupString = "I" + Encoding.encodeVL64(itemMinOwnershipLength) + Encoding.encodeVL64(sessionLength) + Encoding.encodeVL64(sessionExpireLength) + Encoding.encodeVL64(dTable.Rows.Count);

                foreach (DataRow dRow in dTable.Rows)
                {
                    catalogueManager.itemTemplate Template = catalogueManager.getTemplate(Convert.ToInt32(dRow["rclr_reward"]));
                    if (Template.Sprite != "")
                    {
                        sessionRewards.Add(Convert.ToInt32(dRow["rclr_cost"]), Convert.ToInt32(dRow["rclr_reward"]));
                        setupString += Encoding.encodeVL64(Convert.ToInt32(dRow["rclr_cost"])) + "H" + Template.Sprite + Convert.ToChar(2) + "H" + Encoding.encodeVL64(Template.Length) + Encoding.encodeVL64(Template.Width) + Convert.ToChar(2);
                        //setupString += Encoding.encodeVL64(rclrCosts[i]) + "H" + Template.cctName + Convert.ToChar(2) + "HJI" + Convert.ToChar(2);
                    }
                }

                Out.WriteLine("Recycler enabled.");
            }
            else
            {
                setupString = "H";
                Out.WriteLine("Recycler disabled.");
            }
            if (Update)
                Thread.CurrentThread.Abort();
        }
        #region Session management
        /// <summary>
        /// Determines if there exists a reward for this amount of brought in items.
        /// </summary>
        /// <param name="itemCount">The amount of items brought in.</param>
        public static bool rewardExists(int itemCount)
        {
            return sessionRewards.ContainsKey(itemCount);
        }
        /// <summary>
        /// Creates a Recycler session for this user in the users_recycler table, with the userid, the timestamp where the session started and the reward template ID matching this amount of brought of items.
        /// </summary>
        /// <param name="userID">The ID of the user who requests a Recycler session.</param>
        /// <param name="itemCount">The amount of items the user brought in.</param>
        public static void createSession(int userID, int itemCount)
        {
            int rewardTemplateID = ((int)sessionRewards[itemCount]);
            using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
            {
                dbClient.AddParamWithValue("userID", userID);
                dbClient.AddParamWithValue("dateTime", DateTime.Now.ToString());
                dbClient.AddParamWithValue("rewardTemplateID", rewardTemplateID);
                dbClient.runQuery("INSERT INTO users_recycler(userid,session_started,session_reward) VALUES (@userID, @dateTime, @rewardTemplateID)");
            }
        }
        /// <summary>
        /// Deletes the user's session row in users_recycler, and brings items back to the users hand OR deletes them permanently from the database.
        /// </summary>
        /// <param name="userID">The ID of the user who owns this session.</param>
        /// <param name="dropItems">When true, the items will be deleted from database, else the items will return back to the users Hand.</param>
        public static void dropSession(int userID, bool dropItems)
        {
            using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
            {
                dbClient.runQuery("DELETE FROM users_recycler WHERE userid = '" + userID + "' LIMIT 1");
                if (dropItems)
                    dbClient.runQuery("DELETE FROM furniture WHERE ownerid = '" + userID + "' AND roomid = '-2'");
                else
                    dbClient.runQuery("UPDATE furniture SET roomid = '0' WHERE ownerid = '" + userID + "' AND roomid = '-2'");
            }
        }
        /// <summary>
        /// Creates the reward for the user and handles special items such as teleporters, so user receives a pair etc.
        /// </summary>
        /// <param name="userID">The ID of the user to reward the session of.</param>
        public static void rewardSession(int userID)
        {
            int rewardTemplateID = sessionRewardID(userID);
            using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
            {
                dbClient.AddParamWithValue("reward", sessionRewardID(userID));
                dbClient.AddParamWithValue("userID", userID);
                dbClient.runQuery("INSERT INTO furniture(tid,ownerid, roomid) VALUES (@reward, @userID, '0')");
            }
            catalogueManager.handlePurchase(rewardTemplateID, userID, 0, "0", 0, 0);
        }
        /// <summary>
        /// Returns the amount of passed minutes since the user's session started. If the user hasn't got a session or any other error occurs, 0 is returned.
        /// </summary>
        /// <param name="userID">The user ID of the session to lookup.</param>
        public static int passedMinutes(int userID)
        {
            try
            {
                TimeSpan Span;
                using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
                {
                    Span = DateTime.Now - DateTime.Parse(dbClient.getString("SELECT session_started FROM users_recycler WHERE userid = '" + userID + "' LIMIT 1"));
                }
                return Convert.ToInt32(Span.TotalMinutes);
            }
            catch
            {
                return 0;
            }
        }
        /// <summary>
        /// Returns the session string for a users session to use with the "Dp" packet.
        /// </summary>
        /// <param name="userID">The ID of the user to retrieve the session string for.</param>
        public static string sessionString(int userID)
        {
            if (Config.enableRecycler == false)
                return "H";
            using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
            {
                if (dbClient.findsResult("SELECT * FROM users_recycler WHERE userid = '" + userID + "'") == false)
                    return "H";
            }
            int minutesPassed = passedMinutes(userID);
            if (minutesPassed < sessionLength)
                return "IH" + catalogueManager.getTemplate(sessionRewardID(userID)).Sprite + Convert.ToChar(2) + Encoding.encodeVL64(sessionLength - minutesPassed);
            if (minutesPassed > sessionLength)
                return "JH" + catalogueManager.getTemplate(sessionRewardID(userID)).Sprite + Convert.ToChar(2);
            if (minutesPassed > sessionExpireLength)
                return "K";

            return "H";
        }
        /// <summary>
        /// Returns a bool that indicates if a user has a session.
        /// </summary>
        /// <param name="userID">The ID of the user to check for sessions.</param>
        public static bool sessionExists(int userID)
        {
            using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
            {
                return dbClient.findsResult("SELECT userid FROM users_recycler WHERE userid = '" + userID + "'");
            }
        }
        /// <summary>
        /// Returns a bool that indicates if a user's session is ready and hasn't expired yet
        /// </summary>
        /// <param name="userID">The user ID of the user to lookup the session of.</param>
        public static bool sessionReady(int userID)
        {
            if (sessionExists(userID))
            {
                int minutesPassed = passedMinutes(userID);
                if ((minutesPassed > sessionLength) & (minutesPassed < sessionExpireLength))
                    return true;
            }
            return false;
        }
        /// <summary>
        /// Returns the template ID of reward item of the session of a user.
        /// </summary>
        /// <param name="userID">The user ID of the session to lookup.</param>
        private static int sessionRewardID(int userID)
        {
            using (DatabaseClient dbClient = Eucalypt.dbManager.GetClient())
            {
                return dbClient.getInt("SELECT session_reward FROM users_recycler WHERE userid = '" + userID + "'");
            }
        }
        #endregion
    }
}

