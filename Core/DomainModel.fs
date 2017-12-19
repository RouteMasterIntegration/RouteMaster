namespace RouteMaster

open System
open System.Collections.Concurrent
open RouteMaster.Logging
open RouteMaster.Logging.Message

type SubscriptionId = SubscriptionId of string

type RouteName = RouteName of string

type StepName = StepName of string
    with
        member x.Value =
            match x with StepName s -> s

type ProcessId = ProcessId of string
    with
        member x.Value =
            match x with ProcessId p -> p

type CorrelationId = CorrelationId of string
    with
        member x.Value =
            match x with CorrelationId c -> c

type Topic = Topic of string

type MessageBus =
    inherit IDisposable

    abstract member Publish<'a when 'a : not struct> :
        'a -> TimeSpan -> Async<unit>
    abstract member TopicPublish<'a when 'a : not struct> :
        'a -> Topic -> TimeSpan -> Async<unit>
    abstract member Subscribe<'a when 'a : not struct> :
        SubscriptionId -> ('a -> Async<unit>) -> unit
    abstract member TopicSubscribe<'a when 'a : not struct> :
        SubscriptionId -> Topic -> ('a -> Async<unit>) -> unit

type Send =
    abstract member Publish : MessageBus -> Async<unit>
    abstract member Type : Type
    abstract member Message : obj
    abstract member Topic : Topic option
    abstract member Expiry : TimeSpan

type Send<'a when 'a : not struct>(message : 'a, expiry : TimeSpan, topic : Topic option) =
    new(message, expiry) = Send(message, expiry, None)
    new(message, expiry, topic : Topic) = Send(message, expiry, Some topic)

    member val Message = message
    member val Topic = topic
    member val Expiry = expiry

    interface Send with
        member __.Publish pmb =
            match topic with
            | None ->
                pmb.Publish<'a> message expiry
            | Some t ->
                pmb.TopicPublish<'a> message t expiry
        member __.Type = typeof<'a>
        member __.Message = box message
        member __.Topic = topic
        member __.Expiry = expiry

type StateAccess<'a when 'a : not struct> =
    abstract member Update : ('a -> 'a) -> 'a option

type StateStore =
    abstract member Create<'a when 'a : not struct> : ProcessId -> 'a -> unit
    abstract member Access<'a when 'a : not struct> : ProcessId -> StateAccess<'a>
    abstract member Remove<'a when 'a : not struct> : ProcessId -> unit

type TimeoutMessage =
    { TimeoutId : CorrelationId
      ExpectedStep : StepName }

type RegisteredStep<'input, 'state> internal (name) =
    member x.Name : StepName = name

type Expect<'state> =
    abstract member CorrelationId : CorrelationId
    abstract member NextStepName : StepName
    abstract member TimeoutStepName : StepName
    abstract member TimeToLive : TimeSpan

type Expect<'input, 'state> =
    { CorrelationId : CorrelationId
      NextStep : RegisteredStep<'input, 'state>
      TimeoutStep : RegisteredStep<TimeoutMessage, 'state>
      TimeToLive : TimeSpan }
    interface Expect<'state> with
        member x.CorrelationId = x.CorrelationId
        member x.NextStepName = x.NextStep.Name
        member x.TimeoutStepName = x.TimeoutStep.Name
        member x.TimeToLive = x.TimeToLive

type Expected<'state> =
    | Expected of Expect<'state> list
    | Cancel

type StoredExpect =
    { CorrelationId : CorrelationId
      NextStepName : StepName
      TimeoutStepName : StepName
      TimeToLive : TimeSpan
      ProcessId : ProcessId }
    static member OfExpect pid (expect : Expect<_>) =
        { CorrelationId = expect.CorrelationId
          NextStepName = expect.NextStepName
          TimeoutStepName = expect.TimeoutStepName
          TimeToLive = expect.TimeToLive
          ProcessId = pid }

type TimedOut =
    { TimeoutStepName : StepName
      ExpectedStepName : StepName
      ProcessId : ProcessId
      CorrelationId : CorrelationId }

type ExpectedStore =
    abstract member Add : StoredExpect -> Async<unit>
    abstract member Remove : CorrelationId -> StepName -> ProcessId -> Async<unit>
    abstract member GetProcessId : CorrelationId -> StepName -> Async<ProcessId option>
    abstract member IsActive : ProcessId -> Async<bool>
    abstract member Cancel : ProcessId -> Async<unit>
    abstract member TimedOut : (TimedOut -> Async<unit>) -> Async<unit>

type Config =
    { Bus : MessageBus
      StateStore : StateStore
      ExpectedStore : ExpectedStore
      RouteName : RouteName }
    member x.RouteTopic =
        match x.RouteName with
        | RouteName rn -> Topic rn

type RouteBuilder internal (config : Config) =
    member internal __.Config = config
    member val internal Active = true with get, set

type StepResult<'state> =
    { Expected : Expected<'state>
      ToSend : Send list }

type Step<'input, 'state when 'input : not struct and 'state : not struct> =
    { Invoke : StateAccess<'state> -> 'input -> Async<StepResult<'state>>
      ExtractCorrelationId : 'input -> CorrelationId option
      Topic : Topic option
      Name : StepName }
