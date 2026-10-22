using CorreosVelsat.Email;
using CorreosVelsat.Worker;

namespace CorreosVelsat.Tests
{
    public class MailQueueWorkerTests
    {
        [Theory]
        [InlineData(10, 0, 0, true)]   // lote lleno sin fallos: puede haber más, sigue
        [InlineData(7, 0, 0, false)]   // lote incompleto: la cola quedó vacía, espera
        [InlineData(0, 0, 0, false)]   // nada pendiente
        [InlineData(10, 1, 0, false)]  // hubo fallos: espera el intervalo para no gastar intentos
        [InlineData(10, 0, 1, false)]  // sin cupo en los proveedores: espera la próxima pasada
        public void ShouldContinueImmediately_OnlyWhenBatchIsFullAndClean(int fetched, int failed, int deferred, bool expected)
        {
            var batch = new MailQueueWorker.BatchResult(fetched, failed, deferred);

            Assert.Equal(expected, MailQueueWorker.ShouldContinueImmediately(batch, batchSize: 10));
        }

        [Fact]
        public void NoQuotaInAnyProvider_LeavesEmailPending_WithoutCountingAnAttempt()
        {
            var noQuota = EmailSendResult.Fail(EmailErrorKind.RateLimited, "Ningún proveedor de correo disponible");

            Assert.Equal(MailQueueWorker.QueueAction.LeavePending, MailQueueWorker.Decide(noQuota));
        }

        [Theory]
        [InlineData(EmailErrorKind.Validation)]
        [InlineData(EmailErrorKind.Rejected)]
        [InlineData(EmailErrorKind.ProviderError)]
        [InlineData(EmailErrorKind.Authentication)]
        public void RealErrors_CountAnAttempt(EmailErrorKind kind)
        {
            Assert.Equal(MailQueueWorker.QueueAction.CountAttempt, MailQueueWorker.Decide(EmailSendResult.Fail(kind, "x")));
        }

        [Fact]
        public void Success_MarksAsSent()
        {
            Assert.Equal(MailQueueWorker.QueueAction.MarkSent, MailQueueWorker.Decide(new EmailSendResult { Success = true }));
        }
    }
}
