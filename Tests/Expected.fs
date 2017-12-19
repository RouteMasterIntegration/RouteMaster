module RouteMaster.Tests.Expected

open System
open System.Linq
open RouteMaster
open RouteMaster.Process
open RouteMaster.State.PostgreSQL
open Expecto
open Expecto.ExpectoFsCheck

module SafeArbs =
    open FsCheck

    type ExpectArbs =
        static member CorrelationId() =
            gen {
                let guid = Guid.NewGuid()
                return CorrelationId (guid.ToString())
            } |> Arb.fromGen
        static member ProcessId() =
            gen {
                let guid = Guid.NewGuid()
                return ProcessId (guid.ToString())
            } |> Arb.fromGen
        static member StepName() =
            gen {
                let guid = Guid.NewGuid()
                return StepName (guid.ToString())
            } |> Arb.fromGen
        static member TimeSpan() =
            Arb.Default.TimeSpan()
            |> Arb.filter (fun ts -> ts > TimeSpan.Zero)

let expectedTest name testFunc =
    let config = { FsCheckConfig.defaultConfig
                     with arbitrary = [typeof<SafeArbs.ExpectArbs>] }
    let store = getStores () |> snd
    testPropertyWithConfig config name (testFunc store)

[<Tests>]
let expectedStoreTests =
    testList "expected messages store tests" [
        expectedTest "can add a run and it becomes active" <| fun store expected ->
            store.Add expected
            |> Async.RunSynchronously
            let result =
                store.IsActive expected.ProcessId
                |> Async.RunSynchronously
            Expect.isTrue result "RunId should be active"

        expectedTest "can remove a continuation and it stops being active" <| fun store expected ->
            store.Add expected
            |> Async.RunSynchronously
            store.Remove expected.CorrelationId expected.NextStepName expected.ProcessId
            |> Async.RunSynchronously
            let result =
                store.IsActive expected.ProcessId
                |> Async.RunSynchronously
            Expect.isFalse result "RunId should not be active"

        expectedTest "can add and retrieve expected message" <| fun store expected ->
            store.Add expected
            |> Async.RunSynchronously
            let result = store.GetProcessId expected.CorrelationId expected.NextStepName
                         |> Async.RunSynchronously
            Expect.equal result (Some expected.ProcessId) "Expected message should be available"

        expectedTest "multiple continuations from one message" <| fun store cid pid expected1 expected2 ->
            store.Add { expected1 with CorrelationId = cid; ProcessId = pid }
            |> Async.RunSynchronously
            store.Add { expected2 with CorrelationId = cid; ProcessId = pid }
            |> Async.RunSynchronously
            let result1 = store.GetProcessId cid expected1.NextStepName
                          |> Async.RunSynchronously
            let result2 = store.GetProcessId cid expected2.NextStepName
                          |> Async.RunSynchronously
            Expect.equal result1 result2 "Both should be active with the same process ID"

        expectedTest "multiple continuations can be canceled independently" <| fun store cid pid expected1 expected2 ->
            store.Add { expected1 with CorrelationId = cid; ProcessId = pid }
            |> Async.RunSynchronously
            store.Add { expected2 with CorrelationId = cid; ProcessId = pid }
            |> Async.RunSynchronously
            let result1 = store.GetProcessId cid expected1.NextStepName
                          |> Async.RunSynchronously
            store.Remove cid expected2.NextStepName pid
            |> Async.RunSynchronously
            let result2 = store.GetProcessId cid expected2.NextStepName
                          |> Async.RunSynchronously
            Expect.equal result1 (Some pid) "1 should be active"
            Expect.equal result2 None "2 should not be"
    ]
