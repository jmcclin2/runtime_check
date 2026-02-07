using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    const int HEARTBEAT_INTERVAL_SECONDS = 30;
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Usage Tracker Test - Offline Mode ===\n");
        
        UsageTracker.StorageDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "TestData"
        );
        
        Console.Write("Username: ");
        string username = Console.ReadLine() ?? "";
        Console.Write("Password: ");
        string password = Console.ReadLine() ?? "";
        
        Console.Write("Start with online login? (Y/N): ");
        var loginChoice = Console.ReadKey();
        Console.WriteLine("\n");

        LoginResult result;
        if (loginChoice.Key == ConsoleKey.Y)
        {
            Console.WriteLine("Processing online login...");
            result = UsageTracker.ProcessOnlineLogin(username, password);
            Console.WriteLine($"✓ {result.Message}\n");
        }

        Console.WriteLine("Processing offline login...");
        result = UsageTracker.ProcessOfflineLogin(username, password);

        if (!result.Success)
        {
            Console.WriteLine("Initial online login is required before offline use is available!");
            Console.WriteLine("Press 'Q' to quit");
            while (Console.ReadKey(true).Key != ConsoleKey.Q) { }
            return;
        }

        Console.WriteLine($"✓ {result.Message}\n");

        Console.WriteLine("Initial Offline Stats:");
        DisplayStats(result.Stats);
        
        Console.WriteLine($"\n=== Starting Heartbeat (every {HEARTBEAT_INTERVAL_SECONDS} seconds) ===");
        Console.WriteLine("Watch the offline time accumulate!");
        Console.WriteLine("Press 'Q' to quit\n");
        
        using (var cts = new CancellationTokenSource())
        {
            var heartbeatTask = RunHeartbeat(username, password, cts.Token);
            
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Q)
                    {
                        Console.WriteLine("\nStopping heartbeat...");
                        cts.Cancel();
                        break;
                    }
                }
                await Task.Delay(100);
            }
            
            await heartbeatTask;
        }

        var finalResult = UsageTracker.UpdateHeartbeat(username, password);
        Console.WriteLine("\nFinal Usage Stats:");
        DisplayStats(finalResult.Stats);
        
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
    
    static async Task RunHeartbeat(string username, string password, CancellationToken cancellationToken)
    {
        int heartbeatCount = 0;
        
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(HEARTBEAT_INTERVAL_SECONDS * 1000, cancellationToken);
                
                heartbeatCount++;
                Console.WriteLine($"\n--- Heartbeat #{heartbeatCount} at {DateTime.Now:HH:mm:ss} ---");
                
                var result = UsageTracker.UpdateHeartbeat(username, password);
                
                if (!result.Success)
                {
                    Console.WriteLine($"❌ {result.Message}");
                    
                    if (result.ClockManipulationDetected)
                    {
                        Console.WriteLine("🚨 SECURITY ALERT: Clock manipulation detected!");
                        Console.WriteLine("Application is terminating...");
                        await Task.Delay(2000);
                        Environment.Exit(1);
                    }
                    break;
                }
                else
                {
                    Console.WriteLine("✓ Heartbeat successful");
                    DisplayStats(result.Stats);
                }
            }
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Heartbeat stopped gracefully.");
        }
    }
    
    static void DisplayStats(UsageStats? stats)
    {
        if (stats == null)
        {
            Console.WriteLine("❌ Unable to retrieve statistics");
            return;
        }

        if (stats.TimeManipulationDetected)
        {
            Console.WriteLine("⚠️  WARNING: Time manipulation detected!");
        }

        if (stats.IsCurrentlyOnline)
        {
            Console.WriteLine("📶 Status: Online (No time limit)");
        }
        else
        {
            Console.WriteLine($"📊 Offline Mode:");
            Console.WriteLine($"   • Remaining: {stats.RemainingOfflineHours:F2} hours");
            Console.WriteLine($"   • Used: {stats.TotalOfflineHours:F2} hours");
            Console.WriteLine($"   • Session: {stats.CurrentSessionDuration.TotalHours:F2} hours");
            if (stats.RemainingOfflineHours < UsageTracker.MAX_OFFLINE_HOURS * 0.10)
                Console.WriteLine($"   🚨 CRITICAL: Less than {UsageTracker.MAX_OFFLINE_HOURS * 0.10:F1} hours left!");
            else if (stats.RemainingOfflineHours < UsageTracker.MAX_OFFLINE_HOURS * 0.50)
                Console.WriteLine($"   ⚠️  Low: Less than {UsageTracker.MAX_OFFLINE_HOURS * 0.50:F1} hours remaining");
        }
    }
    

}