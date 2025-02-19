using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Servicebus.JobScheduler.Core.Contracts;
using Servicebus.JobScheduler.Core.Contracts.Messages;
using Servicebus.JobScheduler.Core.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Servicebus.JobScheduler.ExampleApp.Emulators
{
    public class InMemoryMessageBus<TTopics, TSubscription> : IMessageBus<TTopics, TSubscription> where TTopics : struct, Enum where TSubscription : struct, Enum
    {
        class DummyMessage : BaseMessage
        {
            public string Id => "1";

            public string RunId => "1";
        }

        enum DummyTopic
        {
        }
        readonly Dictionary<TSubscription, IList> _eventHandlers = new();
        private readonly ILogger _logger;

        public InMemoryMessageBus(ILogger logger)
        {
            _logger = logger;
        }

        public async Task PublishAsync(BaseMessage msg, TTopics topic, DateTime? executeOnUtc = null)
        {
            var scheduledEnqueueTimeUtcDescription = executeOnUtc.HasValue ? executeOnUtc.ToString() : "NOW";

            _logger.LogInformation($"Publishing to {topic} MessageId: {msg.Id} Time to Execute: {scheduledEnqueueTimeUtcDescription}");

            if (executeOnUtc.HasValue)
            {
                int due = (int)(executeOnUtc.Value - DateTime.UtcNow).TotalMilliseconds;
                var _ = new Timer((m) => { publishToSubscribers(m as BaseMessage, topic); }, msg, Math.Max(due, 0), Timeout.Infinite);
            }
            else
            {
                await publishToSubscribers(msg, topic);
            }
        }

        private async Task publishToSubscribers(BaseMessage msg, TTopics topic)
        {
            foreach (var eventHandlersKvp in _eventHandlers)
            {
                if (eventHandlersKvp.Key.ToString().Contains(topic.ToString(), StringComparison.InvariantCultureIgnoreCase))
                {
                    foreach (var h in eventHandlersKvp.Value)
                    {
                        _logger.LogInformation($"Incoming {topic}:{eventHandlersKvp.Key}/{msg.Id} [{msg.GetType().Name}] delegate to {h.GetType().Name}");

                        var handleMethod = h.GetType().GetMethod(nameof(IMessageHandler<DummyTopic, DummyMessage>.Handle));

                        var retries = 0;
                        var success = false;
                        while (!success && retries < 10)
                        {
                            try
                            {
                                var task = handleMethod.Invoke(h, new[] { msg });
                                var handlerResult = (task as Task<HandlerResponse<TTopics>>);
                                var result = await handlerResult;
                                success = true;
                                _logger.LogInformation($"[{h.GetType().Name}] - [{msg.Id}] - [{topic}] Success, result : {result.ToJson()} ");

                                if (result.ContinueWithResult != null)
                                {
                                    // dont wait
                                    await PublishAsync(result.ContinueWithResult.Message, result.ContinueWithResult.TopicToPublish, result.ContinueWithResult.ExecuteOnUtc);
                                }
                                else
                                {
                                    _logger.LogWarning($"[{h.GetType().Name}] - [{msg.Id}] - [{topic}] Got to its final stage!! ");

                                }
                            }
                            catch (System.Exception e)
                            {
                                retries++;
                                _logger.LogError($"[{h.GetType().Name}] - [{msg.Id}] - [{topic}] Handler Failed to handle msg on retry [{retries}] error: {e.Message} ");
                            }
                        }
                    };
                }
            }
        }

        public Task SetupEntitiesIfNotExist(IConfiguration _) => Task.CompletedTask;

        public Task<bool> RegisterSubscriber<TMsg>(TTopics topic, TSubscription subscription, int concurrencyLevel, IMessageHandler<TTopics, TMsg> handler, RetryPolicy<TTopics> deadLetterRetrying, CancellationTokenSource source)
         where TMsg : class, IBaseMessage
        {
            _logger.LogInformation($"Registering {handler.GetType().Name} Subscriber to: {topic}:{subscription}");

            if (!_eventHandlers.TryGetValue(subscription, out var subs))
            {
                subs = new List<IMessageHandler<TTopics, TMsg>>();
            }
            subs.Add(handler);
            _eventHandlers[subscription] = subs;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
