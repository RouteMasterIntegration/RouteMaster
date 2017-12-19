module RouteMaster.Transport.EasyNetQ

open System
open EasyNetQ
open EasyNetQ.Producer
open EasyNetQ.FluentConfiguration
open RouteMaster

let private publish<'a when 'a : not struct> (bus : IBus) (dms : IMessageDeliveryModeStrategy) (ped : Producer.IPublishExchangeDeclareStrategy) message (expire : TimeSpan) topic =
    async {
        let messageType = typeof<'a>
        let expiration = expire.TotalMilliseconds |> int |> string
        let props = MessageProperties(Expiration = expiration, DeliveryMode = dms.GetDeliveryMode(messageType))
        let enqMessage = Message<'a>(message, props)
        let exchange = ped.DeclareExchange(bus.Advanced, messageType, Topology.ExchangeType.Topic)
        let finalTopic =
            match topic with
            | Some (Topic t) ->
                t
            | None ->
                bus.Advanced.Conventions.TopicNamingConvention.Invoke messageType
        return!
            bus.Advanced.PublishAsync(exchange, finalTopic, false, enqMessage)
            |> Async.AwaitTask
    }

type EasyNetQMessageBus(bus : IBus, subscriptionConfiguration : Action<ISubscriptionConfiguration>) =
    let dms = bus.Advanced.Container.Resolve<IMessageDeliveryModeStrategy>()
    let ped = bus.Advanced.Container.Resolve<IPublishExchangeDeclareStrategy>()
    new (bus : IBus) = new EasyNetQMessageBus(bus, Action<ISubscriptionConfiguration> ignore)
    interface IDisposable with
        member __.Dispose() = bus.Dispose()
    interface MessageBus with
        member __.Publish<'a when 'a : not struct> (message : 'a) timeToLive =
            publish bus dms ped message timeToLive None
        member __.TopicPublish<'a when 'a : not struct> (message : 'a) topic timeToLive =
            publish bus dms ped message timeToLive (Some topic)
        member __.Subscribe<'a when 'a : not struct> (SubscriptionId subId) action =
            let asTask (message : 'a) =
                action message
                |> Async.StartAsTask
                :> Threading.Tasks.Task
            bus.SubscribeAsync(subId, Func<_,_> asTask, subscriptionConfiguration) |> ignore
        member __.TopicSubscribe<'a when 'a : not struct> (SubscriptionId subId) (Topic t) action =
            let asTask (message : 'a) =
                action message
                |> Async.StartAsTask
                :> Threading.Tasks.Task
            let updateConfig subConfig =
                subscriptionConfiguration.Invoke subConfig
                subConfig.WithTopic t |> ignore
            bus.SubscribeAsync(subId, Func<_,_>(asTask), Action<_> updateConfig) |> ignore

