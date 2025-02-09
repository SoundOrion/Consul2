using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Consul;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;

class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IConsulClient, ConsulClient>(p => new ConsulClient(config =>
                {
                    config.Address = new Uri("http://localhost:8500"); // Consul サーバーのアドレス
                }));

                services.AddHostedService<ConsulServiceMonitor>(); // バックグラウンドで動作するサービス
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .Build();

        await host.RunAsync();
    }
}

/// <summary>
/// Consul に自身を登録し、プロセスが生存しているか監視する BackgroundService
/// </summary>
public class ConsulServiceMonitor : BackgroundService
{
    private readonly IConsulClient _consulClient;
    private readonly ILogger<ConsulServiceMonitor> _logger;
    private readonly string _serviceId = "process-orders";

    public ConsulServiceMonitor(IConsulClient consulClient, ILogger<ConsulServiceMonitor> logger)
    {
        _consulClient = consulClient;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Consul にサービス登録を開始...");

        await _consulClient.Agent.ServiceRegister(new AgentServiceRegistration
        {
            ID = _serviceId,
            Name = _serviceId,
            Address = "127.0.0.1",
            Port = 0,
            Check = new AgentServiceCheck
            {
                TTL = TimeSpan.FromSeconds(15),
                Notes = "Self-healthcheck process",
                Name = "Self-Check"
            }
        });

        _logger.LogInformation("Consul にサービスを登録しました。");

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsProcessRunning())
            {
                _logger.LogInformation("プロセスは正常に動作中...");
                await _consulClient.Agent.PassTTL($"service:{_serviceId}", "OK");
            }
            else
            {
                _logger.LogError("プロセスが異常終了！Consul にエラーを通知...");
                await _consulClient.Agent.FailTTL($"service:{_serviceId}", "Process not running!");
                break; // ループを抜ける
            }

            await Task.Delay(10000, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("サービスを停止中... Consul から登録解除を実行");
        await _consulClient.Agent.ServiceDeregister(_serviceId);
        _logger.LogInformation("サービスを正常に登録解除しました。");
        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// EXE（自身）が正常に動作しているか確認
    /// </summary>
    private static bool IsProcessRunning()
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



public static class NetworkHelper
{
    /// <summary>
    /// 動作中のネットワークインターフェイスから IPv4 のローカル IP アドレスを取得します。
    /// （ループバックアドレスは除外）
    /// </summary>
    /// <returns>見つかった場合は IPv4 アドレス（文字列）、見つからなければ null</returns>
    public static string GetLocalIPv4Address()
    {
        // 全ネットワークインターフェイスのうち、動作中かつループバックでないものを対象とする
        var activeInterfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                         ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

        foreach (var ni in activeInterfaces)
        {
            var ipProps = ni.GetIPProperties();

            // Unicast アドレスのうち、IPv4 でループバックでないものを選択
            var ipv4Address = ipProps.UnicastAddresses
                .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                             !IPAddress.IsLoopback(ip.Address))
                .Select(ip => ip.Address.ToString())
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(ipv4Address))
            {
                return ipv4Address;
            }
        }

        return null; // IPv4 アドレスが見つからなかった場合
    }
}
