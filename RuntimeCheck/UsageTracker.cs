using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public class UsageTracker
{
    public static string StorageDirectory { get; set; } =
        @"C:\ProgramData\inf\data";

    public const double MAX_OFFLINE_HOURS = 1.0;
}

public class UsageSession
{
    private readonly string _filePath;
    private readonly string _credentialString;

    private class UsageData
    {
        public DateTime FirstLogin { get; set; }
        public DateTime LastLogin { get; set; }
        public DateTime LastOnlineLogin { get; set; }
        public TimeSpan TotalOfflineTime { get; set; }
        public bool IsOnline { get; set; }
        public DateTime SessionStartTime { get; set; }
    }

    public UsageSession(string username, string password)
    {
        _credentialString = $"{username.ToLowerInvariant()}:{password}";

        using (var sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(_credentialString));
            string filename = Convert.ToBase64String(hash)
                .Replace('/', '_')
                .Replace('+', '-')
                .Replace("=", "") + ".dat";

            var dirInfo = Directory.CreateDirectory(UsageTracker.StorageDirectory);
            dirInfo.Attributes |= FileAttributes.Hidden;
            _filePath = Path.Combine(UsageTracker.StorageDirectory, filename);
        }
    }

    private (byte[] key, byte[] iv) DeriveKeyAndIV(byte[] salt)
    {
        using (var pbkdf2 = new Rfc2898DeriveBytes(
            _credentialString, salt, 100000, HashAlgorithmName.SHA256))
        {
            byte[] key = pbkdf2.GetBytes(32);
            byte[] iv = pbkdf2.GetBytes(16);
            return (key, iv);
        }
    }

    private void SaveUsageData(UsageData data)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        var (key, iv) = DeriveKeyAndIV(salt);

        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(data.FirstLogin.Ticks);
            writer.Write(data.LastLogin.Ticks);
            writer.Write(data.LastOnlineLogin.Ticks);
            writer.Write(data.TotalOfflineTime.Ticks);
            writer.Write(data.IsOnline);
            writer.Write(data.SessionStartTime.Ticks);

            byte[] plaintext = ms.ToArray();

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                byte[] encrypted = aes.CreateEncryptor().TransformFinalBlock(plaintext, 0, plaintext.Length);

                using (var outMs = new MemoryStream())
                using (var outWriter = new BinaryWriter(outMs))
                {
                    outWriter.Write(salt);
                    outWriter.Write(encrypted);
                    if (File.Exists(_filePath))
                        File.SetAttributes(_filePath, FileAttributes.Normal);
                    File.WriteAllBytes(_filePath, outMs.ToArray());
                    File.SetAttributes(_filePath, File.GetAttributes(_filePath) | FileAttributes.Hidden);
                }
            }
        }
    }

    private (UsageData? data, bool corrupted) LoadUsageData()
    {
        if (!File.Exists(_filePath))
        {
            Console.WriteLine("\nNo File found!");
            return (null, false);
        }

        byte[] fileData = File.ReadAllBytes(_filePath);
        if (fileData.Length < 16)
        {
            Console.WriteLine("\nIncorrect file length!");
            return (null, true);
        }

        byte[] salt = new byte[16];
        Array.Copy(fileData, 0, salt, 0, 16);
        byte[] encrypted = new byte[fileData.Length - 16];
        Array.Copy(fileData, 16, encrypted, 0, encrypted.Length);

        var (key, iv) = DeriveKeyAndIV(salt);

        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            byte[] decrypted;
            try
            {
                decrypted = aes.CreateDecryptor().TransformFinalBlock(encrypted, 0, encrypted.Length);
            }
            catch
            {
                Console.WriteLine("\nDecryption failed! Data may have been tampered with.");
                return (null, true);
            }

            using (var ms = new MemoryStream(decrypted))
            using (var reader = new BinaryReader(ms))
            {
                try
                {
                    return (new UsageData
                    {
                        FirstLogin = new DateTime(reader.ReadInt64()),
                        LastLogin = new DateTime(reader.ReadInt64()),
                        LastOnlineLogin = new DateTime(reader.ReadInt64()),
                        TotalOfflineTime = new TimeSpan(reader.ReadInt64()),
                        IsOnline = reader.ReadBoolean(),
                        SessionStartTime = new DateTime(reader.ReadInt64())
                    }, false);
                }
                catch
                {
                    Console.WriteLine("\nFile contained invalid data!");
                    return (null, true);
                }
            }
        }
    }

    public LoginResult ProcessOnlineLogin()
    {
        DateTime now = DateTime.Now;
        var (data, corrupted) = LoadUsageData();

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
            SaveUsageData(data);

            return new LoginResult
            {
                Success = true,
                IsFirstLogin = true,
                RemainingOfflineHours = UsageTracker.MAX_OFFLINE_HOURS,
                Message = $"First login successful. You have {UsageTracker.MAX_OFFLINE_HOURS} hour(s) of offline usage."
            };
        }
        else
        {
            data.LastLogin = now;
            data.LastOnlineLogin = now;
            data.TotalOfflineTime = TimeSpan.Zero;
            data.IsOnline = true;
            SaveUsageData(data);

            return new LoginResult
            {
                Success = true,
                IsFirstLogin = false,
                RemainingOfflineHours = UsageTracker.MAX_OFFLINE_HOURS,
                TotalOfflineHoursUsed = 0,
                Message = $"Online login successful. Your {UsageTracker.MAX_OFFLINE_HOURS} hour(s) of offline usage has been reset."
            };
        }
    }

    public LoginResult ProcessOfflineLogin()
    {
        DateTime now = DateTime.Now;
        var (data, corrupted) = LoadUsageData();
    
        if (data == null)
        {
            return new LoginResult
            {
                Success = false,
                Message = corrupted
                    ? "Data file has been tampered with. Please connect to the internet to continue."
                    : "First login must be online. Please connect to the internet."
            };
        }

        if (now < data.LastLogin)
        {
            return new LoginResult
            {
                Success = false,
                TotalOfflineHoursUsed = data.TotalOfflineTime.TotalHours,
                RemainingOfflineHours = UsageTracker.MAX_OFFLINE_HOURS - data.TotalOfflineTime.TotalHours,
                Message = "Clock manipulation detected. System time is earlier than last recorded session. " +
                          "Please connect to the internet to continue."
            };
        }

        if (data.TotalOfflineTime.TotalHours >= UsageTracker.MAX_OFFLINE_HOURS)
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
        SaveUsageData(data);

        double hoursRemaining = UsageTracker.MAX_OFFLINE_HOURS - data.TotalOfflineTime.TotalHours;

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

    public HeartbeatResult UpdateHeartbeat()
    {
        DateTime now = DateTime.Now;
        var (data, corrupted) = LoadUsageData();

        if (data == null)
        {
            return new HeartbeatResult
            {
                Success = false,
                Message = corrupted
                    ? "Data file has been tampered with. Application will now exit."
                    : "No user data found."
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

            if (data.TotalOfflineTime.TotalHours >= UsageTracker.MAX_OFFLINE_HOURS)
            {
                data.LastLogin = now;
                SaveUsageData(data);

                return new HeartbeatResult
                {
                    Success = false,
                    Message = $"Offline usage limit exceeded ({data.TotalOfflineTime.TotalHours:F1} hours used). " +
                              "Please connect to the internet to continue."
                };
            }
        }

        data.LastLogin = now;
        SaveUsageData(data);

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
                RemainingOfflineHours = UsageTracker.MAX_OFFLINE_HOURS - data.TotalOfflineTime.TotalHours,
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
