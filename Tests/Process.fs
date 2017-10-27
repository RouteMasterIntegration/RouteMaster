module RouteMaster.Tests.Process

open System
open RouteMaster.Types
open RouteMaster.Process
open Expecto

type GetTemplate =
    { cid : Guid
      templateId : string }

type Template =
    { cid : Guid
      template : string }

type LookupUserInfo =
    { cid : Guid
      address : string }

type UserInfo =
    { cid : Guid
      name : string }

type RenderEmail =
    { cid : Guid
      template : string
      name : string }

type EmailRendered =
    { cid : Guid
      content : string }

type DelayedSendEmail =
    { cid : Guid
      address : string
      content : string }

type SendEmail =
    { cid : Guid
      address : string
      content : string }

type EmailSent =
    { cid : Guid }

let templateStore getTemplate =
    { template = getTemplate.templateId
      cid = getTemplate.cid }

let nameStore (lookupUserInfo : LookupUserInfo) =
    { name = lookupUserInfo.address.Split('@').[0]
      cid = lookupUserInfo.cid }

let renderer renderEmail =
    { content = sprintf "Hi %s,\n%s" renderEmail.name renderEmail.template
      cid = renderEmail.cid }

let emailSender (sendEmail : SendEmail) =
    { EmailSent.cid = sendEmail.cid }

let delayedSender (sendEmail : DelayedSendEmail) =
    Async.Sleep 5000 |> Async.RunSynchronously
    { EmailSent.cid = sendEmail.cid }

let inline extract message =
    (^msg : (member cid : Guid) message).ToString()
    |> CorrelationId
    |> Some

let addService (bus : MessageBus) serviceFunc =
    bus.Subscribe
        (SubscriptionId "Bob")
        (fun msg ->
              let next = serviceFunc msg
              bus.Publish next <| TimeSpan.FromDays 1.)

let createConfig subId =
    let stateStore, expectedStore = getStores()
    let bus = getBus()
    let config =
        Config.create bus stateStore expectedStore subId
    let add s = addService bus s
    add templateStore
    add nameStore
    add renderer
    add emailSender
    add delayedSender
    config

let routeTest name testBuildFunc initialState expects =
    testCaseAsync name <| async {
        let config = createConfig (SubscriptionId name)
        let latch = Latch.make()
        let buildFunc = testBuildFunc latch
        use routeMaster =
            RouteMaster.activate config buildFunc
        do! RouteMaster.startRoute routeMaster initialState
        let! result = Latch.wait latch
        expects result
    }

// default time to live
let ttl = TimeSpan.FromMinutes 5.

type SendEmailProcessState =
    { ToAddress : string
      Template : string option
      Content : string option
      UserInfo : string option }
    static member Start address =
        { ToAddress = address
          Template = None
          Content = None
          UserInfo = None }

let timeoutStep builder =
    Step.createTimeout (StepName "timeout") (fun _ _ -> async {
        raise <| FailedException "This timeout should not occur"
        return StepResult.cancel
    })
    |> Step.register builder

[<Tests>]
let workflowTests =
    testList "workflow tests" [
        routeTest "Send email" (fun latch builder ->
            let anEmailWasSent =
                Step.create
                    (StepName "an email was sent")
                    extract
                    (fun _ (_ : EmailSent) -> async { Latch.complete latch true
                                                      return StepResult.cancel })
                |> Step.register builder
            let timeout = timeoutStep builder

            (fun () ->
            async {
                let sendEmail =
                    { cid = Guid.NewGuid()
                      address = "bob@example.com"
                      content = "email content" }
                let cid = extract sendEmail
                return StepResult.pipeline ttl timeout sendEmail cid.Value anEmailWasSent
            }))
            ()
            (fun result -> Expect.isTrue result "should fire")

        routeTest "Linear full workflow" (fun latch builder ->
            let receivedTemplate receivedUserInfo timeout =
                let extract (t : Template) =
                    t.cid.ToString()
                    |> CorrelationId
                    |> Some
                let invoke (access : StateAccess<_>) (template : Template) =
                    async {
                        let state = access.Update (fun state -> { state with Template = Some template.template })
                        match state with
                        | Some { ToAddress = a } ->
                            let lookupUserInfo =
                                { cid = Guid.NewGuid()
                                  address = a }
                            let cid = lookupUserInfo.cid.ToString() |> CorrelationId
                            return StepResult.pipeline ttl timeout lookupUserInfo cid receivedUserInfo
                        | _ ->
                            printfn "Failed to retrieve state!"
                            return StepResult.cancel
                    }
                Step.create
                    (StepName "template received")
                    extract
                    invoke

            let receivedUserInfo receivedEmailRendered timeout =
                let extract (u : UserInfo) =
                    u.cid.ToString()
                    |> CorrelationId
                    |> Some
                let invoke (access : StateAccess<_>) (u : UserInfo) =
                    async {
                        let state = access.Update id
                        match state with
                        | Some { Template = Some t } ->
                            let renderEmail =
                                { cid = Guid.NewGuid()
                                  template = t
                                  name = u.name }
                            let cid = renderEmail.cid.ToString() |> CorrelationId
                            return StepResult.pipeline ttl timeout renderEmail cid receivedEmailRendered
                        | _ ->
                            printfn "Failed to retrieve state!"
                            return StepResult.cancel
                    }
                Step.create
                    (StepName "user info received")
                    extract
                    invoke

            let receivedEmailRendered receivedEmailSent timeout =
                let extract (er : EmailRendered) =
                    er.cid.ToString()
                    |> CorrelationId
                    |> Some
                let invoke (access : StateAccess<_>) (er : EmailRendered) =
                    async {
                        let state = access.Update id
                        match state with
                        | Some { ToAddress = a } ->
                            let sendEmail =
                                { cid = Guid.NewGuid()
                                  address = a
                                  content = er.content }
                            let cid = sendEmail.cid.ToString() |> CorrelationId
                            return StepResult.pipeline ttl timeout sendEmail cid receivedEmailSent
                        | _ ->
                            printfn "Failed to retrieve state!"
                            return StepResult.cancel
                    }
                Step.create
                    (StepName "an email was rendered")
                    extract
                    invoke

            let receivedEmailSent =
                Step.create
                    (StepName "anEmailWasSent")
                    (fun (es : EmailSent) ->
                        es.cid.ToString()
                        |> CorrelationId
                        |> Some)
                    (fun access (_ : EmailSent) -> async {
                        Latch.complete latch (access.Update id)
                        return StepResult.cancel
                    })

            let receivedTimeout =
                Step.createTimeout (StepName "timeout") (fun _ _ -> async {
                    printfn "I should probably tell someone this happened."
                    printfn "But I'm only demo code."
                    return StepResult.cancel
                })

            let timeout =
                receivedTimeout
                |> Step.register builder
            let registeredEmailSent =
                Step.register builder receivedEmailSent
            let registeredEmailRendered =
                receivedEmailRendered registeredEmailSent timeout
                |> Step.register builder
            let registeredUserInfo =
                receivedUserInfo registeredEmailRendered timeout
                |> Step.register builder
            let registeredTemplate =
                receivedTemplate registeredUserInfo timeout
                |> Step.register builder

            (fun initialState ->
            async {
                let initialMessage =
                    { cid = Guid.NewGuid()
                      templateId = "My template" }
                let cid = extract initialMessage
                return
                    StepResult.pipeline
                        ttl timeout initialMessage cid.Value registeredTemplate
            }))
            (SendEmailProcessState.Start "bob@example.com")
            (fun result ->
                Expect.isSome result "State should be set"
                let finalState = result.Value
                Expect.equal finalState.ToAddress "bob@example.com" "Address should be correct"
                Expect.equal finalState.Template.Value "My template" "Template should be correct")

        testCaseAsync "Things timeout" <| async {
            // setup
            let config = createConfig (SubscriptionId "Things timeout")
            let timedOut = ref false
            let complete = Latch.make ()

            let shouldFireStep builder =
                Step.createTimeout
                    (StepName "should fire")
                    (fun _ tm -> async { timedOut := true
                                         Latch.complete complete ()
                                         return StepResult.cancel })
                |> Step.register builder

            let expectEmailSent builder =
                Step.create
                    (StepName "anEmailWasSent")
                    extract
                    (fun access (_ : EmailSent) -> async {
                        raise <| FailedException "This workflow should timeout, not complete"
                        return StepResult.cancel
                    })
                |> Step.register builder

            let buildFunc builder =
                let shouldFire : RegisteredStep<_, unit> = shouldFireStep builder
                let expectEmailSent : RegisteredStep<_, unit> = expectEmailSent builder
                (fun () -> async {
                    let outwardMessage : DelayedSendEmail =
                        { cid = Guid.NewGuid()
                          address = "bob@example.com"
                          content = "email content" }
                    let cid = extract outwardMessage
                    return StepResult.pipeline (TimeSpan.FromMilliseconds 10.) shouldFire outwardMessage cid.Value expectEmailSent
                })

            use routeMaster = RouteMaster.activate config buildFunc

            do! RouteMaster.startRoute routeMaster ()

            // wait for the timeout manager...
            do! Latch.wait complete

            Expect.isTrue (!timedOut) "Timeout manager should have fired a timeout message"
        }

        testCaseAsync "The correct timeout fires" <| async {
            // setup
            let config = createConfig (SubscriptionId "The correct timeout fires")
            let shouldHaveFired = ref false
            let shouldNotHaveFired = ref false
            let complete = Latch.make ()

            let shouldFireStep builder =
                Step.createTimeout
                    (StepName "should fire")
                    (fun _ tm -> async { shouldHaveFired := true
                                         Latch.complete complete ()
                                         return StepResult.cancel })
                |> Step.register builder

            let shouldNotFireStep builder =
                Step.createTimeout
                    (StepName "should not fire")
                    (fun _ tm -> async { shouldNotHaveFired := true
                                         Latch.complete complete ()
                                         return StepResult.cancel })
                |> Step.register builder

            let expectEmailSent builder =
                Step.create
                    (StepName "anEmailWasSent")
                    extract
                    (fun access (_ : EmailSent) -> async {
                        raise <| FailedException "This workflow should timeout, not complete"
                        return StepResult.cancel
                    })
                |> Step.register builder

            let buildFunc builder =
                let shouldFire : RegisteredStep<_, unit> = shouldFireStep builder
                let shouldNotFireStep : RegisteredStep<_, unit> = shouldNotFireStep builder
                let expectEmailSent : RegisteredStep<_, unit> = expectEmailSent builder
                (fun () -> async {
                    let outwardMessage : DelayedSendEmail =
                        { cid = Guid.NewGuid()
                          address = "bob@example.com"
                          content = "email content" }
                    let cid = extract outwardMessage
                    return StepResult.pipeline (TimeSpan.FromMilliseconds 10.) shouldFire outwardMessage cid.Value expectEmailSent
                })

            use routeMaster = RouteMaster.activate config buildFunc

            do! RouteMaster.startRoute routeMaster ()

            // wait for the timeout manager...
            do! Latch.wait complete

            Expect.isTrue (!shouldHaveFired) "Timeout manager should have fired a timeout message"
            Expect.isFalse (!shouldNotHaveFired) "Shouldn't have fired"
        }

        testCaseAsync "Can fork and join" <| async {
            let config = createConfig (SubscriptionId "Can fork and Join")
            let latch = Latch.make()

            let sendRenderMessage state timeout emailRendered =
                match state with
                | Some { Template = Some template; UserInfo = Some info } ->
                    let sendRender =
                        { cid = Guid.NewGuid()
                          template = template
                          name = info }
                    let cid = extract sendRender
                    StepResult.pipeline ttl timeout sendRender cid.Value emailRendered
                | Some { Template = None; UserInfo = _ }
                | Some { Template = _; UserInfo = None } ->
                    StepResult.empty
                | None ->
                    failwith "No state found"
                    StepResult.cancel

            let receivedTemplate builder timeout emailRendered =
                Step.create
                    (StepName "template received")
                    extract
                    (fun access (template : Template) -> async {
                        let state =
                            access.Update (fun s ->
                                           { s with Template =
                                                      Some template.template })
                        return sendRenderMessage state timeout emailRendered
                    })
                |> Step.register builder

            let expectName builder timeout emailRendered =
                Step.create
                    (StepName "name received")
                    extract
                    (fun access (info : UserInfo) -> async {
                        let state =
                            access.Update (fun (s : SendEmailProcessState) ->
                                           { s with UserInfo = Some info.name })
                        return sendRenderMessage state timeout emailRendered
                    })
                |> Step.register builder

            let emailRendered builder =
                Step.create
                    (StepName "email rendered")
                    extract
                    (fun access (rendered : EmailRendered) -> async {
                        Latch.complete latch rendered
                        return StepResult.cancel
                    })
                |> Step.register builder

            let buildFunc builder =
                let timeout = timeoutStep builder
                let emailRendered = emailRendered builder
                let receivedTemplate = receivedTemplate builder timeout emailRendered
                let expectName = expectName builder timeout emailRendered
                (fun initialState ->
                async {
                    let cid = Guid.NewGuid()
                    let cid' = CorrelationId (cid.ToString())
                    let getTemplate =
                        { cid = cid
                          templateId = "My template" }
                        |> Send.send ttl
                    let getName =
                        { cid = cid
                          address = initialState.ToAddress }
                        |> Send.send ttl
                    let templateExpect =
                        Expect.create cid' ttl timeout receivedTemplate
                    let nameExpect =
                        Expect.create cid' ttl timeout expectName
                    return { Expected = Expected [templateExpect;nameExpect]
                             ToSend = [getTemplate;getName] }
                })

            use routeMaster = RouteMaster.activate config buildFunc

            do! RouteMaster.startRoute routeMaster (SendEmailProcessState.Start "bob@example.com")

            let! rendered = Latch.wait latch

            Expect.isNotNull (box rendered) "Should have a result"
        }
    ]
