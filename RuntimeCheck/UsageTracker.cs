using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public class UsageTracker
{
    public static string StorageDirectory { get; set; } = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                     "UsageTrackerApp", "Usage");
    
    public const double MAX_OFFLINE_HOURS = 1.0;
    
    private class UsageData
    {
        public DateTime FirstLogin { get; set; }
        public DateTime LastLogin { get; set; }
        public DateTime LastOnlineLogin { get; set; }
        public TimeSpan TotalOfflineTime { get; set; }
        public bool IsOnline { get; set; }
        public DateTime SessionStartTime { get; set; }
    }
    
    private static string GetFilePath(string username, string password)
    {
        using (var sha256 = SHA256.Create())
        {
            string combined = $"{username}:{password}";
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
            string filename = Convert.ToBase64String(hash)
                .Replace('/', '_')
                .Replace('+', '-')
                .Replace("=", "") + ".dat";
            
            Directory.CreateDirectory(StorageDirectory);
            return Path.Combine(StorageDirectory, filename);
        }
    }
    
    private static void SaveUsageData(string username, string password, UsageData data)
    {
        string filePath = GetFilePath(username, password);
        byte[] entropy = Encoding.UTF8.GetBytes($"{username}:{password}");
        
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(data.FirstLogin.Ticks);
            writer.Write(data.LastLogin.Ticks);
            writer.Write(data.LastOnlineLogin.Ticks);
            writer.Write(data.TotalOfflineTime.Ticks);
            writer.Write(data.IsOnline);
            writer.Write(data.SessionStartTime.Ticks);

            byte[] encrypted = ProtectedData.Protect(ms.ToArray(), entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(filePath, encrypted);
        }
    }
    
    private static UsageData? LoadUsageData(string username, string password)
    {
        string filePath = GetFilePath(username, password);
        
        if (!File.Exists(filePath))
            return null;
        
        try
        {
            byte[] entropy = Encoding.UTF8.GetBytes($"{username}:{password}");
            byte[] encrypted = File.ReadAllBytes(filePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
            
            using (var ms = new MemoryStream(decrypted))
            using (var reader = new BinaryReader(ms))
            {
                return new UsageData
                {
                    FirstLogin = new DateTime(reader.ReadInt64()),
                    LastLogin = new DateTime(reader.ReadInt64()),
                    LastOnlineLogin = new DateTime(reader.ReadInt64()),
                    TotalOfflineTime = new TimeSpan(reader.ReadInt64()),
                    IsOnline = reader.ReadBoolean(),
                    SessionStartTime = new DateTime(reader.ReadInt64())
                };
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
    
    public static LoginResult ProcessOnlineLogin(string username, string password)
    {
        DateTime now = DateTime.Now;
        UsageData? data = LoadUsageData(username, password);
        
        if (data == null)
        {
            data = new UsageData
            {
                FirstLogin = now,
                LastLogin = now,
                LastOnlineLogin = now,
                TotalOfflineTime = TimeSpan.Zero,
                IsOnline = true
            };
            SaveUsageData(username, password, data);
            
            return new LoginResult
            {
                Success = true,
                IsFirstLogin = true,
                RemainingOfflineHours = MAX_OFFLINE_HOURS,
                Message = $"First login successful. You have {MAX_OFFLINE_HOURS} hour(s) of offline usage."
            };
        }
        else
        {
            data.LastLogin = now;
            data.LastOnlineLogin = now;
            data.TotalOfflineTime = TimeSpan.Zero;
            data.IsOnline = true;
            SaveUsageData(username, password, data);
            
            return new LoginResult
            {
                Success = true,
                IsFirstLogin = false,
                RemainingOfflineHours = MAX_OFFLINE_HOURS,
                TotalOfflineHoursUsed = 0,
                Message = $"Online login successful. Your {MAX_OFFLINE_HOURS} hour(s) of offline usage has been reset."
            };
        }
    }
    
    public static LoginResult ProcessOfflineLogin(string username, string password)
    {
        DateTime now = DateTime.Now;
        UsageData? data = LoadUsageData(username, password);
        
        if (data == null)
        {
            return new LoginResult
            {
                Success = false,
                Message = "First login must be online. Please connect to the internet."
            };
        }
        
        if (data.TotalOfflineTime.TotalHours >= MAX_OFFLINE_HOURS)
        {
            return new LoginResult
            {
                Success = false,
                TotalOfflineHoursUsed = data.TotalOfflineTime.TotalHours,
                RemainingOfflineHours = 0,
                Message = $"Offline usage limit exceeded ({data.TotalOfflineTime.TotalHours:F1} hours used). " +
                         "Please connect to the internet to continue."
            };
        }

        data.LastLogin = now;
        data.IsOnline = false;
        data.SessionStartTime = now;
        SaveUsageData(username, password, data);

        double hoursRemaining = MAX_OFFLINE_HOURS - data.TotalOfflineTime.TotalHours;
        
        return new LoginResult
        {
            Success = true,
            IsFirstLogin = false,
            RemainingOfflineHours = hoursRemaining,
            TotalOfflineHoursUsed = data.TotalOfflineTime.TotalHours,
            Message = $"Offline login successful. {hoursRemaining:F1} hours remaining.",
            Stats = new UsageStats
            {
                FirstLoginDate = data.FirstLogin,
                LastLoginDate = data.LastLogin,
                LastOnlineLoginDate = data.LastOnlineLogin,
                TotalOfflineHours = data.TotalOfflineTime.TotalHours,
                RemainingOfflineHours = hoursRemaining,
                IsCurrentlyOnline = false,
                CurrentSessionDuration = TimeSpan.Zero,
                TimeManipulationDetected = false
            }
        };
    }
    
    public static HeartbeatResult UpdateHeartbeat(string username, string password)
    {
        DateTime now = DateTime.Now;
        UsageData? data = LoadUsageData(username, password);
        
        if (data == null)
        {
            return new HeartbeatResult
            {
                Success = false,
                Message = "No user data found."
            };
        }
        
        if (!data.IsOnline)
        {
            TimeSpan timeSinceLastUpdate = now - data.LastLogin;

            if (timeSinceLastUpdate < TimeSpan.Zero)
            {
                return new HeartbeatResult
                {
                    Success = false,
                    ClockManipulationDetected = true,
                    Message = "Clock manipulation detected during offline session. Application will now exit."
                };
            }

            data.TotalOfflineTime += timeSinceLastUpdate;
        }

        data.LastLogin = now;
        SaveUsageData(username, password, data);

        return new HeartbeatResult
        {
            Success = true,
            Message = "Heartbeat updated successfully.",
            Stats = new UsageStats
            {
                FirstLoginDate = data.FirstLogin,
                LastLoginDate = data.LastLogin,
                LastOnlineLoginDate = data.LastOnlineLogin,
                TotalOfflineHours = data.TotalOfflineTime.TotalHours,
                RemainingOfflineHours = MAX_OFFLINE_HOURS - data.TotalOfflineTime.TotalHours,
                IsCurrentlyOnline = data.IsOnline,
                CurrentSessionDuration = now - data.SessionStartTime,
                TimeManipulationDetected = false
            }
        };
    }
    
}

public class LoginResult
{
    public bool Success { get; set; }
    public bool IsFirstLogin { get; set; }
    public double RemainingOfflineHours { get; set; }
    public double TotalOfflineHoursUsed { get; set; }
    public string Message { get; set; } = "";
    public UsageStats? Stats { get; set; }
}

public class HeartbeatResult
{
    public bool Success { get; set; }
    public bool ClockManipulationDetected { get; set; }
    public string Message { get; set; } = "";
    public UsageStats? Stats { get; set; }
}

public class UsageStats
{
    public DateTime FirstLoginDate { get; set; }
    public DateTime LastLoginDate { get; set; }
    public DateTime LastOnlineLoginDate { get; set; }
    public double TotalOfflineHours { get; set; }
    public double RemainingOfflineHours { get; set; }
    public bool IsCurrentlyOnline { get; set; }
    public TimeSpan CurrentSessionDuration { get; set; }
    public bool TimeManipulationDetected { get; set; }
}