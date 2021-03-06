﻿using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rebus.Kafka
{
    /// <summary>Example message consumer</summary>
    public sealed class KafkaConsumer : IDisposable
    {
        /// <summary>Subscribes to incoming messages</summary>
        /// <param name="topics">Topics to subscribe to using the given message handler</param>
        /// <param name="action">Incoming message handler</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        [Obsolete]
        public Task Consume(IEnumerable<string> topics, Action<Message<Null, string>> action, CancellationToken cancellationToken)
        {
            _topics = topics;

            Task task = Task.Factory.StartNew(() =>
            {
                _consumer.Subscribe(topics);
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var consumeResult = _consumer.Consume(cancellationToken);
                        if (consumeResult.IsPartitionEOF)
                        {
                            //_logger?.LogInformation($"Reached end of topic {consumeResult.Topic}, partition {consumeResult.Partition}.");
                            continue;
                        }

                        action(consumeResult.Message);
                        //_logger?.LogInformation($"Received message at {consumeResult.TopicPartitionOffset}: {consumeResult.Value}");

                        if (consumeResult.Offset % _commitPeriod == 0)
                        {
                            // The Commit method sends a "commit offsets" request to the Kafka
                            // cluster and synchronously waits for the response. This is very
                            // slow compared to the rate at which the consumer is capable of
                            // consuming messages. A high performance application will typically
                            // commit offsets relatively infrequently and be designed handle
                            // duplicate messages in the event of failure.
                            _consumer.Commit(consumeResult);
                        }
                    }
                }
                catch (ConsumeException e)
                {
                    _logger?.LogError($"Consume error: {e.Error}");
                }
                finally
                {
                    _consumer.Close();
                }
            },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            task.ConfigureAwait(false);
            return task;
        }

        /// <summary>Get observable consumer for subscribe to incoming messages</summary>
        /// <param name="topics">Topics to subscribe to using the given message handler</param>
        /// <returns></returns>
        public IObservable<Message<Null, string>> Consume(IEnumerable<string> topics)
        {
            _topics = topics;
            var observable = Observable.Create<Message<Null, string>>((observer, cancellationToken) =>
            {
                var task = Task.Factory.StartNew(
                    () =>
                    {
                        _consumer.Subscribe(topics);
                        try
                        {
                            while (!cancellationToken.IsCancellationRequested)
                            {

                                var consumeResult = _consumer.Consume(cancellationToken);
                                if (consumeResult.IsPartitionEOF)
                                {
                                    //_logger?.LogInformation($"Reached end of topic {consumeResult.Topic}, partition {consumeResult.Partition}.");
                                    continue;
                                }

                                observer.OnNext(consumeResult.Message);
                                //_logger?.LogInformation($"Received message at {consumeResult.TopicPartitionOffset}: {consumeResult.Value}");

                                if (consumeResult.Offset % _commitPeriod == 0)
                                {
                                    // The Commit method sends a "commit offsets" request to the Kafka
                                    // cluster and synchronously waits for the response. This is very
                                    // slow compared to the rate at which the consumer is capable of
                                    // consuming messages. A high performance application will typically
                                    // commit offsets relatively infrequently and be designed handle
                                    // duplicate messages in the event of failure.
                                    _consumer.Commit(consumeResult);
                                }
                            }
                        }
                        catch (ConsumeException e)
                        {
                            _logger?.LogError($"Consume error: {e.Error}");
                            observer.OnError(e);
                        }
                        finally
                        {
                            _consumer.Close();
                        }
                        observer.OnCompleted();
                    },
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                task.ConfigureAwait(false);
                return task;
            });
            return observable;
        }
		/// <summary>Commits an offset based on the topic/partition/offset of a ConsumeResult.</summary>
		/// <param name="cancellationToken"></param>
		/// <returns>The ConsumeResult instance used to determine the committed offset.</returns>
		public List<TopicPartitionOffset> Commit(CancellationToken cancellationToken) => _consumer.Commit();
		/// <summary>Internal Consumer</summary>
		public IConsumer<Null, string> Consumer => _consumer;

        #region Скучное

        private readonly IConsumer<Null, string> _consumer;
        private readonly int _commitPeriod = 5;
        private readonly ILogger<KafkaConsumer> _logger;
        private IEnumerable<string> _topics;

        /// <summary>Creates new instance <see cref="KafkaConsumer"/>.</summary>
        /// <param name="brokerList">Initial list of brokers as a CSV list of broker host or host:port.</param>
        /// <param name="groupId">Id of group</param>
        /// <param name="logger"></param>
        public KafkaConsumer(string brokerList, string groupId = null, ILogger<KafkaConsumer> logger = null)
        {
            _logger = logger;
            if (string.IsNullOrWhiteSpace(brokerList))
                throw new NullReferenceException(nameof(brokerList));
            var config = new ConsumerConfig
            {
                BootstrapServers = brokerList,
                ApiVersionRequest = true,
                GroupId = !string.IsNullOrEmpty(groupId) ? groupId : Guid.NewGuid().ToString(),
                EnableAutoCommit = false,
                FetchWaitMaxMs = 5,
                FetchErrorBackoffMs = 5,
                QueuedMinMessages = 1000,
                SessionTimeoutMs = 6000,
                //StatisticsIntervalMs = 5000,
#if DEBUG

				Debug = "msg",
#endif
                AutoOffsetReset = AutoOffsetReset.Latest,
                EnablePartitionEof = true
            };
            config.Set("fetch.message.max.bytes", "10240");

            // Note: If a key or value deserializer is not set (as is the case below), the 
            // deserializer corresponding to the appropriate type from Confluent.Kafka.Serdes
            // will be used automatically (where available). The default deserializer for string
            // is UTF8. The default deserializer for Ignore returns null for all input data
            // (including non-null data).
            _consumer = new ConsumerBuilder<Null, string>(config)
                .SetKeyDeserializer(Deserializers.Null)
                .SetValueDeserializer(Deserializers.Utf8)
                .SetLogHandler(OnLog)
                .SetErrorHandler(OnError)
                .SetStatisticsHandler((_, json) => Console.WriteLine($"Statistics: {json}"))
                .SetPartitionsAssignedHandler(ConsumerOnPartitionsAssigned)
                .SetPartitionsRevokedHandler(ConsumerOnPartitionsRevoked)
                .Build();
        }

        /// <summary>Creates new instance <see cref="KafkaConsumer"/>.</summary>
        /// <param name="brokerList">Initial list of brokers as a CSV list of broker host or host:port.</param>
        /// <param name="logger"></param>
        /// <param name="groupId">Id of group</param>
        public KafkaConsumer(string brokerList, ILogger<KafkaConsumer> logger = null, string groupId = null) : this(brokerList, groupId, logger) { }

        /// <summary>Creates new instance <see cref="KafkaConsumer"/>. Allows you to configure
        /// all the parameters of the consumer used in this transport.</summary>
        /// <param name="consumerConfig">A collection of librdkafka configuration parameters
        ///     (refer to https://github.com/edenhill/librdkafka/blob/master/CONFIGURATION.md)
        ///     and parameters specific to this client (refer to:
        ///     <see cref="T:Confluent.Kafka.ConfigPropertyNames" />).
        ///     At a minimum, 'bootstrap.servers' and 'group.id' must be
        ///     specified.</param>
        /// <param name="logger"></param>
        public KafkaConsumer(ConsumerConfig consumerConfig, ILogger<KafkaConsumer> logger = null)
        {
            _logger = logger;
            if (string.IsNullOrWhiteSpace(consumerConfig?.BootstrapServers))
                throw new NullReferenceException($"{nameof(consumerConfig)}.{nameof(consumerConfig.BootstrapServers)}");
            if (string.IsNullOrEmpty(consumerConfig.GroupId))
                consumerConfig.GroupId = Guid.NewGuid().ToString("N");

            // Note: If a key or value deserializer is not set (as is the case below), the 
            // deserializer corresponding to the appropriate type from Confluent.Kafka.Serdes
            // will be used automatically (where available). The default deserializer for string
            // is UTF8. The default deserializer for Ignore returns null for all input data
            // (including non-null data).
            _consumer = new ConsumerBuilder<Null, string>(consumerConfig)
                .SetKeyDeserializer(Deserializers.Null)
                .SetValueDeserializer(Deserializers.Utf8)
                .SetLogHandler(OnLog)
                .SetErrorHandler(OnError)
                .SetStatisticsHandler((_, json) => _logger.LogInformation($"Statistics: {json}"))
                .SetPartitionsAssignedHandler(ConsumerOnPartitionsAssigned)
                .SetPartitionsRevokedHandler(ConsumerOnPartitionsRevoked)
                .Build();
        }

        private void OnLog(object sender, LogMessage logMessage)
        {
            if (!logMessage.Message.Contains("MessageSet size 0, error \"Success\""))//Чтобы не видеть сообщений о пустых чтениях
                _logger?.LogDebug(
                    "Consuming from Kafka. Client: '{client}', syslog level: '{logLevel}', message: '{logMessage}'.",
                    logMessage.Name,
                    logMessage.Level,
                    logMessage.Message);
        }

        private void OnError(IConsumer<Null, string> sender, Error error)
        {
            if (!error.IsFatal)
                _logger?.LogWarning("Consumer error: {error}. No action required.", error);
            else
            {
                var values = sender.Assignment;
                _logger?.LogError(
                    "Fatal error consuming from Kafka. Topic/partition/offset: '{topic}/{partition}/{offset}'. Error: '{error}'.",
                    string.Join(",", values.Select(a => a.Topic)),
                    string.Join(",", values.Select(a => a.Partition)),
                    string.Join(",", values.Select(sender.Position)),
                    error.Reason);
                throw new KafkaException(error);
            }
        }

        private void ConsumerOnPartitionsAssigned(IConsumer<Null, string> sender, List<TopicPartition> partitions)
        {
            _logger?.LogInformation($"Assigned partitions: [{string.Join(", ", partitions.Select(p => p.Partition))}]");
            // possibly override the default partition assignment behavior:
            // consumer.Assign(...) 
        }

        private void ConsumerOnPartitionsRevoked(IConsumer<Null, string> sender, List<TopicPartitionOffset> partitionOffsets)
        {
            _logger?.LogInformation($"Revoked partitions: [{string.Join(", ", partitionOffsets.Select(po => po.Partition))}]");
            // consumer.Unassign()
        }

        /// <inheritdoc />
		public void Dispose()
        {
            if (_consumer != null)
            {
                _consumer.Unsubscribe();
                _consumer.Dispose();
            }
        }

        #endregion
    }
}
