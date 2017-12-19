module RouteMaster.TimeoutManager

open System
open RouteMaster
open RouteMaster.Logging
open RouteMaster.Logging.Message

let internal logger = Log.create "RouteMaster.TimeoutManager"

type TimeoutManagerAnnouncement = TimeoutManagerAnnouncement of Guid

type internal Message =
    | AnnouncementReceived of Guid
    | Tick
    | Stop

[<Measure>]
type Tick

type Guid with
    static member MaxValue =
        Guid(Array.create 16 Byte.MaxValue)

type internal State =
    { Id : Guid
      Active : bool
      Tick : int64<Tick>
      LowestGuidSeen : Guid
      LowestGuidSeenTick : int64<Tick>
      GuidsSeen : Map<Guid, int64<Tick>>
      LastPublish : int64<Tick> }
    static member Empty() =
        { Id = Guid.NewGuid()
          Active = false
          Tick = 0L<Tick>
          LowestGuidSeen = Guid.MaxValue
          LowestGuidSeenTick = 0L<Tick>
          GuidsSeen = Map.empty
          LastPublish = -10L<Tick> }

module internal State =
    let addGuid guid state =
        if guid <= state.LowestGuidSeen then
            { state with
                Active = guid = state.Id
                LowestGuidSeen = guid
                LowestGuidSeenTick = state.Tick
                GuidsSeen = Map.add guid state.Tick state.GuidsSeen }
        else
            { state with
                GuidsSeen = Map.add guid state.Tick state.GuidsSeen }

    let cleanOld state =
        let liveMap =
            Map.filter (fun _ t -> t + 15L<Tick> < state.Tick) state.GuidsSeen
        if state.LowestGuidSeenTick + 15L<Tick> < state.Tick then
            match Map.toSeq liveMap |> Seq.sortBy fst |> Seq.tryHead with
            | Some (guid, tick) ->
                { state with
                    Active = guid = state.Id
                    LowestGuidSeen = guid
                    LowestGuidSeenTick = tick
                    GuidsSeen = liveMap }
            | None ->
                // If we reach here, we're not even seeing our own announcement
                // messages - something is wrong...
                Message.event Warn "Manager {managerId} is not receiving timeout manager announcements"
                |> Message.setField "managerId" state.Id
                |> logger.logSimple
                { state with
                    Active = true
                    LowestGuidSeen = state.Id
                    LowestGuidSeenTick = state.Tick
                    GuidsSeen = liveMap }
        else state

    let increment state =
        { state with Tick = state.Tick + 1L<Tick> }


let internal checkPublishAnnoucement topic (bus : MessageBus) state =
    async {
        if state.LastPublish + 10L<Tick> <= state.Tick then
            do! bus.TopicPublish
                    (TimeoutManagerAnnouncement state.Id)
                    topic
                    (TimeSpan.FromSeconds 15.)
            return { state with LastPublish = state.Tick }
        else
            return state
    }

let publishTimeout topic (bus : MessageBus) (expectedStore : ExpectedStore) (t : TimedOut) =
    async {
        let cid = Guid.NewGuid().ToString() |> CorrelationId
        let toStore =
            { CorrelationId = cid
              NextStepName = t.TimeoutStepName
              TimeoutStepName = StepName "RouteMaster Default Timeout"
              TimeToLive = TimeSpan.FromDays 1.
              ProcessId = t.ProcessId }
        let toSend =
            { TimeoutId = cid
              ExpectedStep = t.ExpectedStepName }
        do!
            eventX "Expected response for {processId} has timed out: {timedOut}"
            >> setField "timedOut" t
            >> setField "processId" t.ProcessId
            |> logger.infoWithBP

        do! expectedStore.Add toStore
        do! bus.TopicPublish toSend topic (TimeSpan.FromDays 1.)
    }

let internal startTimeoutManager (config : Config) =
    let publishTimeoutMessage =
        publishTimeout config.RouteTopic config.Bus config.ExpectedStore
    let rec loop state (agent : MailboxProcessor<Message>) =
        async {
            let! msg = agent.Receive()
            match msg with
            | Stop -> ()
            | AnnouncementReceived guid ->
                let next =
                    State.addGuid guid state
                return! loop next agent
            | Tick ->
                let! next =
                    state
                    |> State.increment
                    |> State.cleanOld
                    |> checkPublishAnnoucement config.RouteTopic config.Bus
                if state.Active then
                    do! config.ExpectedStore.TimedOut publishTimeoutMessage
                return! loop next agent
        }
    let start = State.Empty()
    let agent = MailboxProcessor.Start (loop start)
    agent.Error.Add raise
    config.Bus.TopicSubscribe
        (SubscriptionId <| start.Id.ToString())
        config.RouteTopic
        (fun (TimeoutManagerAnnouncement guid) ->
            async { agent.Post <| AnnouncementReceived guid })
    let rec ticker () =
        async {
            agent.Post Message.Tick
            do! Async.Sleep 1000
            return! ticker ()
        }
    Async.Start (ticker ())
    agent
