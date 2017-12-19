using System;
using System.Threading.Tasks;
using Expecto;
using Expecto.CSharp;
using RouteMaster;
using RouteMaster.Extensions;
using static RouteMaster.State.Memory;
using static RouteMaster.Transport.Memory;
using static RouteMaster.Tests.Process;
using static RouteMaster.Tests.Setup;

namespace RouteMaster.Tests.CSharp
{
    public class EmailRouteState {
        public string ToAddress { get; set; }
        public string Template { get; set; }
        public string Content { get; set; }
        public string UserInfo { get; set; }

        public EmailRouteState(string toAddress) {
            ToAddress = toAddress;
        }
    }

    public class RouteStarter : Starter<EmailRouteState> {
        private RegisteredStep<TimeoutMessage, EmailRouteState> _timeout;
        private RegisteredStep<EmailSent, EmailRouteState> _next;

        public RouteStarter(RegisteredStep<TimeoutMessage, EmailRouteState> timeOut,
                            RegisteredStep<EmailSent, EmailRouteState> nextStep) {
            _timeout = timeOut;
            _next = nextStep;
        }

        public Task<StepResult<EmailRouteState>> Start(EmailRouteState ers) {
            var guid = Guid.NewGuid();
            var cid = CorrelationId.NewCorrelationId(guid.ToString());
            var sendEmail = new SendEmail(guid, ers.ToAddress, "Hi Bob!");
            return
                StepResult.Pipeline(cid,
                                    sendEmail,
                                    _next,
                                    RouteTest.ttl,
                                    _timeout);
        }
    }

    public class RouteTest : BuildLogic<EmailRouteState> {
        private TaskCompletionSource<bool> _tcs;
        private Step<EmailSent, EmailRouteState> _anEmailWasSent;

        public RouteTest(TaskCompletionSource<bool> tcs) {
            _tcs = tcs;
            _anEmailWasSent =
              Step.Create<EmailSent, EmailRouteState>
                  (StepName.NewStepName("AnEmailWasSent"),
                  (es => new MaybeCorrelationId(es.cid.ToString())),
                  (access, es) => this.emailWasSentLogic(access, es));
        }

        public static Config CreateTestConfig(string subId) {
            return createConfig(RouteName.NewRouteName(subId));
        }

        public static TimeSpan ttl = TimeSpan.FromMinutes(5);

        private static Task<StepResult<EmailRouteState>> timeoutLogic(StateAccess<EmailRouteState> access, TimeoutMessage tm) {
            return Task.Run(() => StepResult.Cancel<EmailRouteState>());
        }

        private static Step<TimeoutMessage, EmailRouteState> _timeoutStep =
            Step.CreateTimeout<EmailRouteState>
             (StepName.NewStepName("Default Timeout"),
             (access, tm) => timeoutLogic(access, tm));

        private Task<StepResult<EmailRouteState>> emailWasSentLogic(StateAccess<EmailRouteState> access, EmailSent es) {
            Console.WriteLine("Hey! An email was sent: {0}", es);
            _tcs.SetResult(true);
            return Task.Run(() => StepResult.Cancel<EmailRouteState>());
        }

        public Starter<EmailRouteState> Build(RouteBuilder rb) {
            return new RouteStarter(_timeoutStep.Register(rb), _anEmailWasSent.Register(rb));
        }

        [Tests]
        public static Test Tests =
            Runner.TestList("RouteTests", new Test [] {
                    Runner.TestCase("Start off a route", async () => {
                            var tcs = new TaskCompletionSource<bool>();
                            var config = CreateTestConfig("subId");
                            var route = config.BuildRoute<EmailRouteState>(new RouteTest(tcs));
                            await route.Start(new EmailRouteState("bob@example.com"));
                            var result = await tcs.Task;
                            Expect.isTrue(result, "Should be true");
                        })
                });
    }
}
