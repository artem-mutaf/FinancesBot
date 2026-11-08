using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FinancesTelegramBot.Bot;

public sealed class DevTunnelOptions
{
    public const string SectionName = "DevTunnel";

    /// <summary>Включить автоматический туннель (только в Development).</summary>
    public bool Enabled { get; init; }

    /// <summary>Локальный порт приложения, который пробрасываем наружу.</summary>
    public int LocalPort { get; init; } = 5276;
}

/// <summary>
/// ТОЛЬКО ДЛЯ РАЗРАБОТКИ. Сам поднимает HTTPS-туннель через localhost.run:
///   1. запускает  ssh -R 80:localhost:5276 nokey@localhost.run  как дочерний процесс;
///   2. читает его вывод и ищет строку с адресом https://xxxx.lhr.life;
///   3. сообщает адрес в WebAppUrlProvider (а тот обновляет кнопку меню бота);
///   4. если ssh отвалился — ждёт и переподключается.
///
/// "partial" нужен для [GeneratedRegex]: регулярное выражение компилируется
/// source-генератором на этапе сборки, а не при каждом запуске.
/// </summary>
public sealed partial class DevTunnelService(
    WebAppUrlProvider urlProvider,
    IOptions<DevTunnelOptions> options,
    ILogger<DevTunnelService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Если прошлый запуск приложения убили жёстко (кнопка Stop в IDE), его ssh мог остаться жить.
    /// Запоминаем PID нашего ssh в файле, чтобы при следующем старте добить "сироту".
    /// </summary>
    private static readonly string PidFile = Path.Combine(Path.GetTempPath(), "FinancesTelegramBot-devtunnel.pid");

    [GeneratedRegex(@"https://[a-z0-9-]+\.lhr\.life", RegexOptions.IgnoreCase)]
    private static partial Regex TunnelUrlRegex();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        KillOrphanedTunnel();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTunnelAsync(stoppingToken);
                logger.LogWarning("Туннель отключился. Переподключение через {Delay} c…", ReconnectDelay.TotalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // приложение останавливается — это нормально
            }
            catch (Win32Exception)
            {
                logger.LogError("Не найден ssh. Включи OpenSSH Client: Параметры → Система → Дополнительные компоненты");
                return; // без ssh повторять бессмысленно
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка туннеля");
            }

            try { await Task.Delay(ReconnectDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Один сеанс туннеля: от запуска ssh до его завершения.</summary>
    private async Task RunTunnelAsync(CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,      // запускаем напрямую, чтобы перехватить ввод/вывод
            CreateNoWindow = true,        // без отдельного консольного окна
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // stdin держим открытым: если он закроется, ssh завершит сеанс
        };
        // ArgumentList сам экранирует аргументы — безопаснее, чем склеивать строку.
        foreach (var arg in new[]
                 {
                     "-o", "StrictHostKeyChecking=accept-new", // ключ сервера принять без вопроса (только 1-й раз)
                     "-o", "ServerAliveInterval=30",           // пинг раз в 30 c, чтобы сервер не рвал соединение
                     "-o", "ExitOnForwardFailure=yes",         // не удалось пробросить порт — завершиться, а не висеть
                     "-o", "BatchMode=yes",                    // никогда не спрашивать пароль (зависли бы навсегда)
                     "-R", $"80:localhost:{options.Value.LocalPort}",
                     "nokey@localhost.run",
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Не удалось запустить ssh");
        await File.WriteAllTextAsync(PidFile, process.Id.ToString(), ct);
        logger.LogInformation("Запускаю туннель (ssh, PID {Pid})…", process.Id);

        try
        {
            // Читаем stdout и stderr параллельно: если не читать один из потоков,
            // его буфер переполнится и ssh "встанет" (классическая ловушка с Process).
            // stdout — баннер localhost.run (шумный, в Debug); stderr — ошибки ssh (важно видеть).
            var stdout = WatchOutputAsync(process.StandardOutput, LogLevel.Debug, ct);
            var stderr = WatchOutputAsync(process.StandardError, LogLevel.Warning, ct);

            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
        }
        finally
        {
            // Выходим по любой причине (в т.ч. остановка приложения) — ssh не должен остаться жить.
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            File.Delete(PidFile);
        }
    }

    /// <summary>Построчно читает вывод ssh и ловит в нём адрес туннеля.</summary>
    private async Task WatchOutputAsync(StreamReader reader, LogLevel level, CancellationToken ct)
    {
        // "is { } line" — читаем, пока ReadLineAsync не вернёт null (поток закончился).
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                logger.Log(level, "ssh: {Line}", line);

            var match = TunnelUrlRegex().Match(line);
            if (match.Success && match.Value != urlProvider.Url)
            {
                try
                {
                    await urlProvider.SetAsync(match.Value, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Telegram недоступен и т.п. — туннель-то работает, просто логируем.
                    logger.LogWarning(ex, "Не удалось обновить кнопку меню бота");
                }
            }
        }
    }

    private void KillOrphanedTunnel()
    {
        try
        {
            if (!File.Exists(PidFile) || !int.TryParse(File.ReadAllText(PidFile), out var pid))
                return;

            using var orphan = Process.GetProcessById(pid);
            // Проверяем имя: PID мог уже достаться другой программе — её трогать нельзя.
            if (orphan.ProcessName.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                orphan.Kill();
                logger.LogInformation("Остановлен старый туннель (PID {Pid})", pid);
            }
        }
        catch (ArgumentException)
        {
            // Процесса с таким PID уже нет — отлично.
        }
        finally
        {
            File.Delete(PidFile);
        }
    }
}
