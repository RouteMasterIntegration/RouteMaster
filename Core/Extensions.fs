namespace RouteMaster.Extensions

open System
open System.Runtime.CompilerServices
open System.Threading.Tasks
open RouteMaster.Process
open RouteMaster.Types

type Starter<'state> =
    abstract Start : 'state -> Task<StepResult<'state>>

type BuildLogic<'state> =
    abstract Build : RouteBuilder -> Starter<'state>

[<Extension>]
type ConfigExtension() =
    [<Extension>]
    static member BuildRoute(config, buildLogic : BuildLogic<'state>) =
        let asyncBuild rb =
            let starter (built : Starter<'state>) s =
                built.Start(s) |> Async.AwaitTask

            let built = buildLogic.Build(rb)
            starter built
        RouteMaster.activate config asyncBuild

[<Extension>]
type RouteMasterExtension() =
    [<Extension>]
    static member Start(routeMaster, initialState) =
        RouteMaster.startRoute routeMaster initialState
        |> Async.StartAsTask

type MaybeCorrelationId private (cid : Option<CorrelationId>) =
    new () = MaybeCorrelationId(None)
    new (str : string) = MaybeCorrelationId(Some <| CorrelationId str)
    member internal __.Cid = cid

type Step() =
    static member private ExtractF (extract : Func<'a, MaybeCorrelationId>) a =
        extract.Invoke(a).Cid

    static member private InvokeF (invoke : Func<StateAccess<'state>, 'a, Task<StepResult<'state>>>) sa a =
        invoke.Invoke(sa, a)
        |> Async.AwaitTask

    static member Create(name, extract, invoke, topic) =
        Step.createWithTopic name (Step.ExtractF extract) topic (Step.InvokeF invoke)


    static member Create(name, extract, invoke) =
        Step.create name (Step.ExtractF extract) (Step.InvokeF invoke)

    static member CreateTimeout(name, invoke) =
        Step.createTimeout name (Step.InvokeF invoke)

[<Extension>]
type StepExtension() =
    [<Extension>]
    static member Register(step, routeBuilder) =
        Step.register routeBuilder step

type StepResult() =
    static member Empty() = { Expected = Expected []; ToSend = [] }
    static member Cancel() = { Expected = Cancel; ToSend = [] }
    static member Pipeline(correlationId, toSend, nextStep, timeToLive, timeoutStep) =
        StepResult.pipeline timeToLive timeoutStep toSend correlationId nextStep
    static member Pipeline(correlationId, toSend, topic, nextStep, timeToLive, timeoutStep) =
        StepResult.pipelineToTopic timeToLive timeoutStep toSend topic correlationId nextStep

[<Extension>]
type StepResultExtension() =
    [<Extension>]
    static member AddToSend(result, s : #Send) =
        { result with ToSend = (s :> Send)::result.ToSend }

    [<Extension>]
    static member AddExpected(result, correlationId, nextStep, timeToLive, timeoutStep) =
        match result.Expected with
        | Expected existing ->
            let e = Expect.create correlationId timeToLive timeoutStep nextStep
            { result with Expected = Expected (e::existing) }
        | Cancel ->
            failwith "You cannot add expectations to a step which cancels a route."
