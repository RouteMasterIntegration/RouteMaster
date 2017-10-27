module RouteMaster.Transport.Memory

open System
open RouteMaster.Types

type private Subscriber =
    abstract Action : obj -> Async<unit>
    abstract Type : Type
    abstract Binding : string

type private Subscriber<'a> =
    { SubscriptionId : SubscriptionId
      Binding : string
      Action : 'a -> Async<unit> }
    interface Subscriber with
        member x.Action o =
            o |> unbox<'a> |> x.Action
        member __.Type =
            typeof<'a>
        member x.Binding =
            x.Binding

type private BusMessage =
    | Publish of obj * Type * DateTime * Topic option
    | Subscribe of Subscriber
    | Stop of AsyncReplyChannel<unit>

let private compareSection (topicSection : string, bindingSection : string) =
    match bindingSection with
    | "#" | "*" -> true
    | _ when bindingSection = topicSection -> true
    | _ -> false

let private topicBindingMatch topicOpt (binding : string) =
    match topicOpt with
    | Some (Topic topic) ->
        let topicSections = topic.Split '.'
        let bindingSections = binding.Split '.'
        if bindingSections.[bindingSections.Length - 1] = "#" then
            Seq.zip topicSections bindingSections
            |> Seq.forall compareSection
        else
            if bindingSections.Length = topicSections.Length then
                Seq.zip topicSections bindingSections
                |> Seq.forall compareSection
            else
                false
    | None ->
        binding = "#"

let rec private loop subscribers (exiting : AsyncReplyChannel<unit> option) (agent : MailboxProcessor<BusMessage>) =
    async {
        match exiting with
        | Some chan when agent.CurrentQueueLength = 0 ->
            return chan.Reply()
        | _ ->
            let! msg = agent.Receive()
            match msg with
            | Stop chan ->
                return! loop subscribers (Some chan) agent
            | Subscribe s ->
                return! loop (s::subscribers) exiting agent
            | Publish (message, type', expireTime, topic) ->
                if expireTime > DateTime.UtcNow then
                    let matchingSubs =
                        subscribers
                        |> List.filter (fun x -> type' = x.Type
                                                  && topicBindingMatch topic x.Binding)
                    for sub in matchingSubs do
                        sub.Action message |> Async.StartImmediate
                return! loop subscribers exiting agent
    }

type MemoryBus () =
    let agent = MailboxProcessor.Start(loop [] None)
    do agent.Error.Add raise
    interface IDisposable with
        member __.Dispose() =
            agent.PostAndReply Stop
    interface MessageBus with
        member __.Publish (message : 'a) expiry =
            agent.Post (Publish (box message, typeof<'a>, DateTime.UtcNow + expiry, None))
            async.Zero()
        member __.TopicPublish (message : 'a) topic expiry =
            agent.Post (Publish (box message, typeof<'a>, DateTime.UtcNow + expiry, Some topic))
            async.Zero()
        member __.Subscribe sid action =
            agent.Post (Subscribe { SubscriptionId = sid; Binding = "#"; Action = action })
        member __.TopicSubscribe sid (Topic binding) action =
            agent.Post (Subscribe { SubscriptionId = sid; Binding = binding; Action = action })
