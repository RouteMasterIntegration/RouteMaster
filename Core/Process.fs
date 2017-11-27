module RouteMaster.Process

open System
open RouteMaster.Types
open RouteMaster.Logging
open RouteMaster.Logging.Message

let internal logger = Log.create "RouteMaster.Process"

module Send =
    let send timeToLive message = Send(message, timeToLive) :> Send

    let topicSend timeToLive topic message =
        Send(message, timeToLive, Some topic) :> Send

module Expect =
    let create cid timeToLive timeoutStep nextStep =
        { CorrelationId = cid
          NextStep = nextStep
          TimeoutStep = timeoutStep
          TimeToLive = timeToLive }
        :> Expect<_>

module StepResult =
    let empty = { Expected = Expected []; ToSend = [] }

    let cancel = { Expected = Cancel; ToSend = [] }

    let private generalPipeline timeToLive timeout message topic correlationId next =
        let expect =
            Expect.create correlationId timeToLive timeout next
        let send =
            match topic with
            | Some t ->
                Send.topicSend timeToLive t message
            | None ->
                Send.send timeToLive message
        { Expected = Expected [expect]
          ToSend = [send] }

    let pipeline timeToLive timeoutStep message correlationId next =
        generalPipeline timeToLive timeoutStep message None correlationId next

    let pipelineToTopic timeToLive timeoutStep message topic correlationId next =
        generalPipeline timeToLive timeoutStep message (Some topic) correlationId next

    let internal processResult<'state when 'state : not struct> config pid (result : StepResult<'state>) =
        async {
          match result.Expected with
          | Expected exs ->
              let toStore =
                  exs
                  |> List.map (StoredExpect.OfExpect pid)
              for storedExpect in toStore do
                  do!
                      eventX "Add expected response for:\n{processId}\n{correlationId}\n{stepName}\nwith timeout {timeoutStep}"
                      >> setField "processId" pid
                      >> setField "stepName" storedExpect.NextStepName
                      >> setField "correlationId" storedExpect.CorrelationId
                      >> setField "timeoutStep" storedExpect.TimeoutStepName
                      |> logger.infoWithBP
                  do! config.ExpectedStore.Add storedExpect
          | Cancel ->
              do!
                  eventX "{processId} canceled"
                  >> setField "processId" pid
                  |> logger.infoWithBP
              do! config.ExpectedStore.Cancel pid
          for send in result.ToSend do
              do!
                  eventX "Sending message:\n{message} for {processId}"
                  >> setField "message" (send.Message.ToString())
                  >> setField "processId" pid
                  |> logger.infoWithBP
              do! send.Publish config.Bus
          let! active = config.ExpectedStore.IsActive pid
          if not active then
              config.StateStore.Remove<'state> pid
        }

module Step =
    let create name extract invoke =
        { Invoke = invoke
          ExtractCorrelationId = extract
          Topic = None
          Name = name }

    let createTimeout name invoke =
        { Invoke = invoke
          ExtractCorrelationId = fun { TimeoutId = cid } -> Some cid
          Topic = None
          Name = name }

    let createWithTopic name extract topic invoke =
        { create name extract invoke with Topic = Some topic }

    let register (routeBuilder : RouteBuilder) (step : Step<'input, 'state>) : RegisteredStep<'input, 'state> =
        // All registration steps should happen before the builder is
        // marked as "inactive"
        if not routeBuilder.Active then
            failwith """You have attempted to register a Step after leaving the RouteMaster.activate function.
This can cause unpredictable errors; please make sure all registrations are completed within the activate function."""
        let config = routeBuilder.Config
        let fullName =
            match step.Name with
            | StepName name ->
                match routeBuilder.Config.RouteName with
                | SubscriptionId subId ->
                    StepName (subId + "::" + name)
        let callback input =
            async {
                do!
                    eventX "Received {typeName}, checking if active for {stepName}"
                    >> setField "stepName" fullName
                    >> setField "typeName" (typeof<'input>.Name)
                    |> logger.infoWithBP
                match step.ExtractCorrelationId input with
                | Some cid ->
                    let! maybeProcessId = config.ExpectedStore.GetProcessId cid fullName
                    match maybeProcessId with
                    | Some pid ->
                        do!
                            eventX "Activating {stepName} in {processId} for:\n{message}"
                            >> setField "stepName" fullName
                            >> setField "message" input
                            >> setField "processId" pid
                            |> logger.infoWithBP
                        let access = config.StateStore.Access pid
                        let! result = step.Invoke access input
                        do! StepResult.processResult<'state> config pid result
                        do!
                            eventX "Completed {stepName} in {processId}"
                            >> setField "stepName" fullName
                            >> setField "processId" pid
                            |> logger.infoWithBP
                        do! config.ExpectedStore.Remove cid fullName pid
                        do!
                            eventX "Removed expected response for {stepName} - {processId} from storage"
                            >> setField "stepName" fullName
                            >> setField "processId" pid
                            |> logger.infoWithBP
                    | None ->
                        return ()
                | None ->
                    return ()
            }
        let stepSubId =
            let (SubscriptionId garageName) = config.RouteName
            let (StepName stepName) = step.Name
            SubscriptionId (garageName + "::" + stepName)
        eventX "Registering step {stepName} with {stepSubId}"
        >> setField "stepName" fullName
        >> setField "stepSubId" stepSubId
        |> logger.infoWithBP
        |> Async.RunSynchronously
        match step.Topic with
        | Some t ->
            config.Bus.TopicSubscribe stepSubId t callback
        | None ->
            config.Bus.Subscribe stepSubId callback
        RegisteredStep(fullName)

module RouteMaster =
    let activate config buildFunc : RouteMaster<'state> =
        let builder = RouteBuilder(config)
        let starter = buildFunc builder
        let rm = new RouteMaster<_>(config, starter)
        builder.Active <- false
        rm

    let startRoute (routeMaster : RouteMaster<_>) input =
        async {
            let processId = ProcessId (Guid.NewGuid().ToString())
            routeMaster.ActiveConfig.StateStore.Create processId input
            let! stepResult = routeMaster.Starter input
            do! StepResult.processResult routeMaster.ActiveConfig processId stepResult
        }

module Config =
    let create (bus : #MessageBus) (stateStore : #StateStore) (expectedStore : #ExpectedStore) name =
        { Bus = bus
          StateStore = stateStore
          ExpectedStore = expectedStore
          RouteName = name }
