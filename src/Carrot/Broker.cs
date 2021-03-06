using System;
using System.Collections.Generic;
using System.Linq;
using Carrot.Configuration;
using Carrot.Messages;
using RabbitMQ.Client;

namespace Carrot
{
    public class Broker : IBroker
    {
        private readonly EnvironmentConfiguration _configuration;

        private readonly ISet<Queue> _queues = new HashSet<Queue>();
        private readonly ISet<Exchange> _exchanges = new HashSet<Exchange>();
        private readonly ISet<ExchangeBinding> _bindings = new HashSet<ExchangeBinding>();
        private readonly ISet<Func<IConsumedMessageBuilder, ConsumingPromise>> _promises = new HashSet<Func<IConsumedMessageBuilder, ConsumingPromise>>();

        private readonly IConnectionBuilder _connectionBuilder;

        protected internal Broker(EnvironmentConfiguration configuration, IConnectionBuilder connectionBuilder)
        {
            _configuration = configuration;
            _connectionBuilder = connectionBuilder;
        }

        public static IBroker New(Action<EnvironmentConfiguration> configure)
        {
            return New(configure, new ConnectionBuilder(new DateTimeProvider()));
        }

        public static IBroker New(Action<EnvironmentConfiguration> configure,
                                  IConnectionBuilder connectionBuilder)
        {
            if (configure == null)
                throw new ArgumentNullException(nameof(configure));

            if (connectionBuilder == null)
                throw new ArgumentNullException(nameof(connectionBuilder));

            var configuration = new EnvironmentConfiguration();
            configure(configuration);
            return new Broker(configuration, connectionBuilder);
        }

        public Queue DeclareQueue(String name)
        {
            return DeclareQueue(name, false);
        }

        public Queue DeclareDurableQueue(String name)
        {
            return DeclareQueue(name, true);
        }

        public Exchange DeclareDirectExchange(String name)
        {
            return DeclareExchange(name, "direct", false);
        }

        public Exchange DeclareDurableDirectExchange(String name)
        {
            return DeclareExchange(name, "direct", true);
        }

        public Exchange DeclareFanoutExchange(String name)
        {
            return DeclareExchange(name, "fanout", false);
        }

        public Exchange DeclareDurableFanoutExchange(String name)
        {
            return DeclareExchange(name, "fanout", true);
        }

        public Exchange DeclareTopicExchange(String name)
        {
            return DeclareExchange(name, "topic", false);
        }

        public Exchange DeclareDurableTopicExchange(String name)
        {
            return DeclareExchange(name, "topic", true);
        }

        public Exchange DeclareHeadersExchange(String name)
        {
            return DeclareExchange(name, "headers", false);
        }

        public Exchange DeclareDurableHeadersExchange(String name)
        {
            return DeclareExchange(name, "headers", true);
        }

        public void DeclareExchangeBinding(Exchange exchange, Queue queue, String routingKey = "")
        {
            if (exchange == null)
                throw new ArgumentNullException(nameof(exchange));

            if (queue == null)
                throw new ArgumentNullException(nameof(queue));

            if (routingKey == null)
                throw new ArgumentNullException(nameof(routingKey));

            if (!_bindings.Add(new ExchangeBinding(exchange, queue, routingKey)))
                throw new ArgumentException("dupicate binding detected");
        }

        public Boolean TryDeclareExchangeBinding(Exchange exchange, Queue queue, String routingKey = "")
        {
            if (queue == null)
                throw new ArgumentNullException(nameof(queue));

            if (routingKey == null)
                throw new ArgumentNullException(nameof(routingKey));

            return _bindings.Add(new ExchangeBinding(exchange, queue, routingKey));
        }

        public IConnection Connect()
        {
            var connection = _connectionBuilder.CreateConnection(_configuration.EndpointUri);
            var outboundModel = connection.CreateModel();

            ApplyEntitiesDeclarations(outboundModel);

            return CreateConnection(connection, outboundModel);
        }

        public void SubscribeByAtMostOnce(Queue queue, Action<ConsumingConfiguration> configure)
        {
            Subscribe(configure,
                      (b, c) => new AtMostOnceConsumingPromise(queue, b, c, () => _configuration.Log),
                      queue);
        }

        public void SubscribeByAtLeastOnce(Queue queue, Action<ConsumingConfiguration> configure)
        {
            Subscribe(configure,
                      (b, c) => new AtLeastOnceConsumingPromise(queue, b, c, () => _configuration.Log),
                      queue);
        }

        protected internal virtual IModel CreateInboundModel(RabbitMQ.Client.IConnection connection,
                                                             UInt32 prefetchSize,
                                                             UInt16 prefetchCount)
        {
            var model = connection.CreateModel();
            model.BasicQos(prefetchSize, prefetchCount, false);
            return model;
        }

        private void Subscribe(Action<ConsumingConfiguration> configure,
                               Func<IConsumedMessageBuilder, ConsumingConfiguration, ConsumingPromise> func,
                               Queue queue)
        {
            var configuration = new ConsumingConfiguration(this, queue);
            configure(configuration);
            Func<IConsumedMessageBuilder, ConsumingPromise> f = _ => func(_, configuration);
            _promises.Add(f);
        }

        private Queue DeclareQueue(String name, Boolean isDurable)
        {
            var queue = new Queue(name, isDurable);
            _queues.Add(queue);
            return queue;
        }

        private Exchange DeclareExchange(String name, String type, Boolean isDurable)
        {
            var exchange = new Exchange(name, type, isDurable);
            _exchanges.Add(exchange);
            return exchange;
        }

        private void ApplyEntitiesDeclarations(IModel outboundModel)
        {
            foreach (var exchange in _exchanges)
                exchange.Declare(outboundModel);

            foreach (var queue in _queues)
                queue.Declare(outboundModel);

            foreach (var binding in _bindings)
                binding.Declare(outboundModel);
        }

        private IConnection CreateConnection(RabbitMQ.Client.IConnection connection,
                                             IModel outboundModel)
        {
            var builder = new ConsumedMessageBuilder(_configuration.SerializationConfiguration,
                                                     _configuration.MessageTypeResolver);
            var outboundChannel = _configuration.OutboundChannelBuilder(outboundModel,
                                                                        _configuration);
            var consumers = _promises.Select(_ =>
                                                {
                                                    var model = CreateInboundModel(connection,
                                                                                   _configuration.PrefetchSize,
                                                                                   _configuration.PrefetchCount);
                                                    var consumer = _(builder).BuildConsumer(new InboundChannel(model),
                                                                                            outboundChannel);
                                                    return new { Model = model, Consumer = consumer };
                                                })
                                     .ToList();

            foreach (var consumer in consumers)
                consumer.Consumer.Declare(consumer.Model);

            return new Connection(connection,
                                  consumers.Select(_ => _.Consumer),
                                  outboundChannel,
                                  _configuration);
        }
    }
}