using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sms.Shared.Kernel.Auth;

/// Drains the email queue and delivers via IEmailSender, out-of-band from the request.
public sealed class EmailDispatchWorker(
    IEmailQueue queue,
    IEmailSender sender,
    ILogger<EmailDispatchWorker> logger,
    int maxAttempts = 3,
    TimeSpan? retryDelay = null) : BackgroundService
{
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            EmailMessage message;
            try { message = await queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await sender.SendAsync(message with { Attempt = attempt }, stoppingToken);
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt == maxAttempts || SmtpFailure.IsPermanent(ex))
                    {
                        logger.LogError(ex,
                            "Failed to send email to {To} after {Attempts} attempts; dropping.",
                            message.To, maxAttempts);
                        break;
                    }
                    logger.LogWarning(ex,
                        "Email send to {To} failed (attempt {Attempt}/{Max}); retrying.",
                        message.To, attempt, maxAttempts);
                    try { await Task.Delay(_retryDelay, stoppingToken); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
    }
}
