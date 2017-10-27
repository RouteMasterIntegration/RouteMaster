module RouteMaster.Tests.Bus

open System
open RouteMaster.Types
open Expecto

type T1 = T1 of string
type T2 = T2 of string
type T2b = T2b of string
type T3 = T3 of string
type T4 = T4 of string
type T5 = T5 of string


let busTest name test =
    let bus = getBus ()
    let latch = Latch.make ()
    let subId = SubscriptionId name
    testCaseAsync name (test bus subId latch)

[<Tests>]
let busTests =
    testList "bus tests" [
        busTest "Basic send/subscribe works" <| fun bus subId latch -> async {
                bus.Subscribe<T1> subId (fun (T1 m) -> async { Latch.complete latch m })
                do! bus.Publish (T1 "message") (TimeSpan.FromMinutes 1.)
                let! result = Latch.wait latch
                return Expect.equal result "message" "Should match"
            }

        busTest "Subscribe filters correctly by type" <| fun bus subId latch -> async {
                bus.Subscribe<T2> subId (fun (T2 m) -> async { Latch.complete latch <| Some m })
                do! bus.Publish (T2b "message") (TimeSpan.FromMinutes 1.)
                Async.Start (async {
                    do! Async.Sleep 1000
                    Latch.complete latch None
                })
                let! result = Latch.wait latch
                return Expect.equal result None "Should match"
            }

        busTest "Can publish to topic" <| fun bus subId latch -> async {
                bus.TopicSubscribe<T3> subId (Topic "one.two")
                    (fun (T3 m) -> async { Latch.complete latch <| Some m })
                do! bus.TopicPublish (T3 "message") (Topic "one.two") (TimeSpan.FromMinutes 1.)
                let! result = Latch.wait latch
                return Expect.equal result (Some "message") "Should match"
            }

        busTest "Only receives from matching topic" <| fun bus subId latch -> async {
                bus.TopicSubscribe<T4> subId (Topic "one.two")
                    (fun (T4 m) -> async { Latch.complete latch <| Some m })
                do! bus.TopicPublish (T4 "message") (Topic "two.one") (TimeSpan.FromMinutes 1.)
                let! result = Latch.wait latch
                return Expect.equal result None "Should match"
            }

        busTest "Matching wildcard topic is matched" <| fun bus subId latch -> async {
                bus.TopicSubscribe<T5> subId (Topic "*.two") (fun (T5 m) -> async { Latch.complete latch <| Some m })
                do! bus.TopicPublish (T5 "message") (Topic "one.two") (TimeSpan.FromMinutes 1.)
                let! result = Latch.wait latch
                return Expect.equal result (Some "message") "Should match"
            }
    ]
