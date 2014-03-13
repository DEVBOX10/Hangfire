using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using Dapper;

namespace HangFire.SqlServer
{
    internal class SqlJobLock : IDisposable
    {
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
        private const string LockMode = "Exclusive";
        private const string LockOwner = "Session";

        private static readonly IDictionary<int, string> LockErrorMessages
            = new Dictionary<int, string>
            {
                { -1, "The lock request timed out" },
                { -2, "The lock request was canceled" },
                { -3, "The lock request was chosen as a deadlock victim" },
                { -999, "Indicates a parameter validation or other call error" }
            };

        private readonly SqlConnection _connection;
        private readonly string _resource;

        private bool _completed;

        public SqlJobLock(string jobId, SqlConnection connection)
        {
            _connection = connection;
            _resource = String.Format("HangFire:Job:{0}", jobId);

            var parameters = new DynamicParameters();
            parameters.Add("@Resource", _resource);
            parameters.Add("@LockMode", LockMode);
            parameters.Add("@LockOwner", LockOwner);
            parameters.Add("@LockTimeout", LockTimeout.TotalMilliseconds);
            parameters.Add("@Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

            connection.Execute(
                @"sp_getapplock", 
                parameters, 
                commandType: CommandType.StoredProcedure);

            var lockResult = parameters.Get<int>("@Result");

            if (lockResult < 0)
            {
                throw new SqlServerApplicationLockException(
                    String.Format(
                    "Could not place a lock on the resource '{0}': {1}.",
                    _resource,
                    LockErrorMessages.ContainsKey(lockResult) 
                        ? LockErrorMessages[lockResult]
                        : String.Format("Server returned the '{0}' error.", lockResult)));
            }
        }

        public void Dispose()
        {
            if (_completed) return;

            _completed = true;

            var parameters = new DynamicParameters();
            parameters.Add("@Resource", _resource);
            parameters.Add("@LockOwner", LockOwner);
            parameters.Add("@Result", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);

            _connection.Execute(
                @"sp_releaseapplock",
                parameters,
                commandType: CommandType.StoredProcedure);

            var releaseResult = parameters.Get<int>("@Result");

            if (releaseResult < 0)
            {
                throw new SqlServerApplicationLockException(
                    String.Format(
                        "Could not release a lock on the resource '{0}': Server returned the '{1}' error.", 
                        _resource,
                        releaseResult));
            }
        }
    }
}