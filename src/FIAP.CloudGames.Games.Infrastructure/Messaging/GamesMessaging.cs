using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace FIAP.CloudGames.Games.Infrastructure.Messaging
{
    public class AuthenticationRequestEvent
    {
        public Guid RequestId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public DateTime RequestedAt { get; set; }
    }

    public class AuthenticationResponseEvent
    {
        public Guid RequestId { get; set; }
        public bool Success { get; set; }
        public Guid? UserId { get; set; }
        public string? Token { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime RespondedAt { get; set; }
    }

    public class PaymentCreatedEvent
    {
        public Guid PaymentId { get; set; }
        public Guid OrderId { get; set; }
        public Guid UserId { get; set; }
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public interface IAuthenticationRequestPublisher
    {
        Task<Guid> PublishAuthenticationRequestAsync(string email, string password);
    }

    public class AuthenticationRequestPublisher : IAuthenticationRequestPublisher
    {
        private readonly IAmazonSimpleNotificationService _sns;
        private readonly ILogger<AuthenticationRequestPublisher> _logger;
        private readonly string _topicArn;

        public AuthenticationRequestPublisher(
            IAmazonSimpleNotificationService sns,
            IConfiguration configuration,
            ILogger<AuthenticationRequestPublisher> logger)
        {
            _sns = sns;
            _logger = logger;
            _topicArn = configuration["AWS:SNS:AuthRequestsTopicArn"]
                ?? throw new ArgumentNullException("AWS:SNS:AuthRequestsTopicArn not configured");
        }

        public async Task<Guid> PublishAuthenticationRequestAsync(string email, string password)
        {
            var requestId = Guid.NewGuid();

            try
            {
                var authRequest = new AuthenticationRequestEvent
                {
                    RequestId = requestId,
                    Email = email,
                    Password = password,
                    RequestedAt = DateTime.UtcNow
                };

                var messageJson = JsonSerializer.Serialize(authRequest);

                var request = new PublishRequest
                {
                    TopicArn = _topicArn,
                    Message = messageJson,
                    MessageAttributes = new Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>
                    {
                        {
                            "MessageType",
                            new Amazon.SimpleNotificationService.Model.MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = "AuthenticationRequest"
                            }
                        },
                        {
                            "RequestId",
                            new Amazon.SimpleNotificationService.Model.MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = requestId.ToString()
                            }
                        }
                    }
                };

                var response = await _sns.PublishAsync(request);

                _logger.LogInformation(
                    "[AUTH-REQUEST] Published authentication request for user={Email}, RequestId={RequestId}, MessageId={MessageId}",
                    email,
                    requestId,
                    response.MessageId);

                return requestId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[AUTH-REQUEST] Error publishing authentication request for user={Email}, RequestId={RequestId}",
                    email,
                    requestId);
                throw;
            }
        }
    }

    public interface IPaymentEventPublisher
    {
        Task PublishPaymentCreatedAsync(PaymentCreatedEvent payment);
    }

    public class PaymentEventPublisher : IPaymentEventPublisher
    {
        private readonly IAmazonSimpleNotificationService _sns;
        private readonly ILogger<PaymentEventPublisher> _logger;
        private readonly string _topicArn;

        public PaymentEventPublisher(
            IAmazonSimpleNotificationService sns,
            IConfiguration configuration,
            ILogger<PaymentEventPublisher> logger)
        {
            _sns = sns;
            _logger = logger;
            _topicArn = configuration["AWS:SNS:PaymentEventsTopicArn"]
                ?? throw new ArgumentNullException("AWS:SNS:PaymentEventsTopicArn not configured");
        }

        public async Task PublishPaymentCreatedAsync(PaymentCreatedEvent payment)
        {
            try
            {
                var messageJson = JsonSerializer.Serialize(payment);

                var request = new PublishRequest
                {
                    TopicArn = _topicArn,
                    Message = messageJson,
                    MessageAttributes = new Dictionary<string, Amazon.SimpleNotificationService.Model.MessageAttributeValue>
                    {
                        {
                            "MessageType",
                            new Amazon.SimpleNotificationService.Model.MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = "PaymentCreated"
                            }
                        },
                        {
                            "PaymentId",
                            new Amazon.SimpleNotificationService.Model.MessageAttributeValue
                            {
                                DataType = "String",
                                StringValue = payment.PaymentId.ToString()
                            }
                        }
                    }
                };

                var response = await _sns.PublishAsync(request);

                _logger.LogInformation(
                    "[PAYMENT-EVENT] Published payment created event: PaymentId={PaymentId}, MessageId={MessageId}",
                    payment.PaymentId,
                    response.MessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[PAYMENT-EVENT] Error publishing payment created event: PaymentId={PaymentId}",
                    payment.PaymentId);
                throw;
            }
        }
    }

    public interface IAuthenticationResponseCache
    {
        void StoreResponse(Guid requestId, AuthenticationResponseEvent response);
        Task<AuthenticationResponseEvent?> WaitForResponseAsync(Guid requestId, TimeSpan timeout);
    }

    public class AuthenticationResponseCache : IAuthenticationResponseCache
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AuthenticationResponseEvent>> _pendingRequests = new();

        public void StoreResponse(Guid requestId, AuthenticationResponseEvent response)
        {
            if (_pendingRequests.TryGetValue(requestId, out var tcs))
            {
                tcs.TrySetResult(response);
            }
        }

        public async Task<AuthenticationResponseEvent?> WaitForResponseAsync(Guid requestId, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<AuthenticationResponseEvent>();
            _pendingRequests[requestId] = tcs;

            try
            {
                using var cts = new CancellationTokenSource(timeout);
                cts.Token.Register(() => tcs.TrySetCanceled());

                return await tcs.Task;
            }
            catch (TaskCanceledException)
            {
                return null;
            }
            finally
            {
                _pendingRequests.TryRemove(requestId, out _);
            }
        }
    }

    public class AuthenticationResponseConsumer : BackgroundService
    {
        private readonly IAmazonSQS _sqs;
        private readonly IAuthenticationResponseCache _cache;
        private readonly ILogger<AuthenticationResponseConsumer> _logger;
        private readonly string _queueUrl;

        public AuthenticationResponseConsumer(
            IAmazonSQS sqs,
            IAuthenticationResponseCache cache,
            IConfiguration configuration,
            ILogger<AuthenticationResponseConsumer> logger)
        {
            _sqs = sqs;
            _cache = cache;
            _logger = logger;
            _queueUrl = configuration["AWS:SQS:AuthResponsesQueueUrl"]
                ?? throw new ArgumentNullException("AWS:SQS:AuthResponsesQueueUrl not configured");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[AUTH-RESPONSE-CONSUMER] Starting authentication response consumer");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var request = new ReceiveMessageRequest
                    {
                        QueueUrl = _queueUrl,
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 20,
                        MessageAttributeNames = new List<string> { "All" },
                        AttributeNames = new List<string> { "All" }
                    };

                    var response = await _sqs.ReceiveMessageAsync(request, stoppingToken);

                    if (response.Messages != null)
                        foreach (var message in response.Messages)
                        {
                            await ProcessMessageAsync(message, stoppingToken);
                        }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AUTH-RESPONSE-CONSUMER] Error receiving messages from SQS");
                    await Task.Delay(5000, stoppingToken);
                }
            }

            _logger.LogInformation("[AUTH-RESPONSE-CONSUMER] Stopping authentication response consumer");
        }

        private async Task ProcessMessageAsync(Message message, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[AUTH-RESPONSE-CONSUMER] Processing message: {MessageId}", message.MessageId);

                var snsWrapper = JsonSerializer.Deserialize<SnsMessageWrapper>(message.Body);
                if (snsWrapper == null)
                {
                    _logger.LogWarning("[AUTH-RESPONSE-CONSUMER] Failed to deserialize SNS wrapper");
                    await DeleteMessageAsync(message);
                    return;
                }

                var authResponse = JsonSerializer.Deserialize<AuthenticationResponseEvent>(snsWrapper.Message);
                if (authResponse == null)
                {
                    _logger.LogWarning("[AUTH-RESPONSE-CONSUMER] Failed to deserialize authentication response");
                    await DeleteMessageAsync(message);
                    return;
                }

                _cache.StoreResponse(authResponse.RequestId, authResponse);

                await DeleteMessageAsync(message);

                _logger.LogInformation(
                    "[AUTH-RESPONSE-CONSUMER] Processed authentication response: RequestId={RequestId}, Success={Success}",
                    authResponse.RequestId,
                    authResponse.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[AUTH-RESPONSE-CONSUMER] Error processing message: {MessageId}",
                    message.MessageId);
            }
        }

        private async Task DeleteMessageAsync(Message message)
        {
            try
            {
                await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AUTH-RESPONSE-CONSUMER] Error deleting message: {MessageId}", message.MessageId);
            }
        }
    }

    public class SnsMessageWrapper
    {
        public string Message { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
    }
}