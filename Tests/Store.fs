module RouteMaster.Tests.Store

open System
open RouteMaster
open Expecto

type T1 = T1 of string

let storeTest name testFunc =
    let store, _ = getStores()
    testCase name (fun () -> testFunc store)

[<Tests>]
let storeTests =
    testList "store tests" [
        storeTest "We can store and retrieve stuff" <| fun testStore ->
            let runId = ProcessId "bob"
            testStore.Create runId "A string"
            let access = testStore.Access<string> runId
            let result = access.Update id
            Expect.equal result (Some "A string") "It should be possible to retrieve values"

        storeTest "We can update values in the store" <| fun testStore ->
            let runId = ProcessId "bob"
            testStore.Create runId (T1 "24")
            let access = testStore.Access<T1> runId
            let result = access.Update (fun _ -> T1 "42")
            Expect.equal result (Some <| T1 "42") "It should be possible to retrieve updated values"

        storeTest "We can remove values from the store" <| fun testStore ->
            let runId = ProcessId "bob"
            testStore.Create runId (T1 "24")
            testStore.Remove<T1> runId
            let access = testStore.Access<T1> runId
            let result = access.Update (fun _ -> T1 "42")
            Expect.equal result None "It should be possible to retrieve updated values"
    ]
