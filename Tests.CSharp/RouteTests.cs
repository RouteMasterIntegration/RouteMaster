using System;
using System.Threading.Tasks;
using Expecto;
using Expecto.CSharp;
using RouteMaster.Types;
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

        public async Task<StepResult<EmailRouteState>> Start(EmailRouteState ers) {
            var guid = Guid.NewGuid();
            var cid = CorrelationId.NewCorrelationId(guid.ToString());
            var sendEmail = new SendEmail(guid, ers.ToAddress, "Hi Bob!");
            return
                StepResult.Pipeline(cid,
                                    new Send<SendEmail>(sendEmail, RouteTest.ttl),
                                    _next,
                                    RouteTest.ttl,
                                    _timeout);
        }
    }

    public class RouteTest : BuildLogic<EmailRouteState> {
        public static Config CreateTestConfig(string subId) {
            return createConfig(SubscriptionId.NewSubscriptionId(subId));
        }

        public static TimeSpan ttl = TimeSpan.FromMinutes(5);

        private static Task<StepResult<EmailRouteState>> timeoutLogic(StateAccess<EmailRouteState> access, TimeoutMessage tm) {
            return Task.Run(() => StepResult.Cancel<EmailRouteState>());
        }

        private static Step<TimeoutMessage, EmailRouteState> _timeoutStep =
            Step.CreateTimeout<EmailRouteState>
             (StepName.NewStepName("Default Timeout"),
             (access, tm) => timeoutLogic(access, tm));

        private static Task<StepResult<EmailRouteState>> emailWasSentLogic(StateAccess<EmailRouteState> access, EmailSent es) {
            Console.WriteLine("Hey! An email was sent: {0}", es);
            return Task.Run(() => StepResult.Cancel<EmailRouteState>());
        }

        private static Step<EmailSent, EmailRouteState> _anEmailWasSent =
            Step.Create<EmailSent, EmailRouteState>
            (StepName.NewStepName("AnEmailWasSent"),
             (es => new MaybeCorrelationId(es.cid.ToString())),
             (access, es) => emailWasSentLogic(access, es));

        public Starter<EmailRouteState> Build(RouteBuilder rb) {
            return new RouteStarter(_timeoutStep.Register(rb), _anEmailWasSent.Register(rb));
        }

        [Tests]
        public static Test Tests =
            Runner.TestList("RouteTests", new Test [] {
                    Runner.TestCase("Start off a route", () => {
                            var config = CreateTestConfig("subId");
                            var route = config.BuildRoute<EmailRouteState>(new RouteTest());
                            route.Start(new EmailRouteState("bob@example.com"));
                            System.Threading.Thread.Sleep(1000);
                        })
                });
    }
}
