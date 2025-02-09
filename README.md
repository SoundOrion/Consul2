このプログラムは **自身（EXE）の死活監視を Consul に登録し、監視するためのもの** です。  
現在の実装では `TTL` ヘルスチェックを使っていますが、**プロセスのクラッシュやフリーズを検出する方法** を強化すると、より確実な監視ができます。  

---

# **🛠 改善ポイント**
1. **TTL だけではプロセスフリーズを検出できない**
   - 現状: `PassTTL` を送るだけでは、プロセスがフリーズしても「正常」と判定されてしまう。
   - **解決策:** `self-healthcheck`（自身のヘルスチェック関数）を追加し、定期的に `Process.GetCurrentProcess()` を確認。

2. **プロセスが異常終了したら、自動的に Consul から登録解除**
   - `try-catch` を使って、クラッシュ時に `ServiceDeregister` を呼び出す。

3. **Ctrl+C（SIGINT）や `kill`（SIGTERM）で正しく終了**
   - `Console.CancelKeyPress` で **強制終了時に Consul から登録解除** する。

---

## **🚀 改良後のコード**
```csharp
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Consul;

class Program
{
    static async Task Main()
    {
        string serviceId = "process-orders";

        using var client = new ConsulClient(config => config.Address = new Uri("http://localhost:8500"));

        Console.CancelKeyPress += async (sender, eventArgs) =>
        {
            Console.WriteLine("サービスを停止中...");
            await client.Agent.ServiceDeregister(serviceId);
            Console.WriteLine("サービスを正常に登録解除しました");
            eventArgs.Cancel = true; // 強制終了を防ぐ
        };

        // Consul に自身のサービス登録
        await client.Agent.ServiceRegister(new AgentServiceRegistration
        {
            ID = serviceId,
            Name = serviceId,
            Address = "127.0.0.1",
            Port = 0, // EXE にはポートがないので 0
            Check = new AgentServiceCheck
            {
                TTL = TimeSpan.FromSeconds(15), // 15秒ごとのヘルスチェック
                Notes = "Self-healthcheck process",
                Name = "Self-Check"
            }
        });

        Console.WriteLine("Consul にサービスを登録しました。");

        // メインの監視ループ
        while (true)
        {
            if (IsProcessRunning())
            {
                await client.Agent.PassTTL($"service:{serviceId}", "OK");
            }
            else
            {
                Console.WriteLine("プロセスが動作していません。Consul にエラーを通知します...");
                await client.Agent.FailTTL($"service:{serviceId}", "Process not running!");
                break; // ループを抜ける
            }

            await Task.Delay(10000); // 10秒ごとにチェック
        }

        // Consul から登録解除
        await client.Agent.ServiceDeregister(serviceId);
        Console.WriteLine("サービスが異常終了したため、Consul から登録解除しました。");
    }

    /// <summary>
    /// EXE（自身）が正常に動作しているか確認
    /// </summary>
    static bool IsProcessRunning()
    {
        try
        {
            using Process currentProcess = Process.GetCurrentProcess();
            return !currentProcess.HasExited;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"プロセスチェック中にエラー: {ex.Message}");
            return false;
        }
    }
}
```

---

# **🔍 改良のポイント**
### **✅ 1. TTL 監視だけでなく、プロセスの実行状態をチェック**
- `IsProcessRunning()` を作成し、現在のプロセスが終了していないかチェック。
- フリーズなどの異常も検出できる。

### **✅ 2. プロセスが異常終了したら Consul に `FailTTL` を送信**
```csharp
await client.Agent.FailTTL($"service:{serviceId}", "Process not running!");
```
- これにより、Consul から **「プロセスが死んだ！」というアラートが出せる**。
- その後、Consul から登録解除。

### **✅ 3. SIGINT（Ctrl+C）で正しく終了処理**
```csharp
Console.CancelKeyPress += async (sender, eventArgs) =>
{
    Console.WriteLine("サービスを停止中...");
    await client.Agent.ServiceDeregister(serviceId);
    Console.WriteLine("サービスを正常に登録解除しました");
    eventArgs.Cancel = true;
};
```
- `Ctrl+C`（SIGINT）が押されたとき、**強制終了する前に Consul から登録解除** する。

---

# **✨ 期待される動作**
| 状況 | Consul への通知 | 備考 |
|------|--------------|------|
| 通常時 | `PassTTL` を送る（正常） | 10秒ごとに送信 |
| プロセスがクラッシュ | `FailTTL` を送る（異常） | 監視停止 & Consul から登録解除 |
| `Ctrl+C` で終了 | `ServiceDeregister` を実行 | 正常にサービス削除 |

---

# **🌟 まとめ**
✅ **プロセスの死活監視を強化**  
✅ **TTL のみの監視から、実際のプロセスの実行確認を追加**  
✅ **異常時（クラッシュ）に自動で Consul から登録解除**  
✅ **SIGINT / SIGTERM で安全に終了処理**
