using CorreosVelsat.Worker;

namespace CorreosVelsat.Tests
{
    public class MailQueueWorkerTests
    {
        [Theory]
        [InlineData(10, 0, true)]   // lote lleno sin fallos: puede haber más, sigue
        [InlineData(7, 0, false)]   // lote incompleto: la cola quedó vacía, espera
        [InlineData(0, 0, false)]   // nada pendiente
        [InlineData(10, 1, false)]  // hubo fallos: espera el intervalo para no gastar intentos
        public void ShouldContinueImmediately_OnlyWhenBatchIsFullAndClean(int fetched, int failed, bool expected)
        {
            var batch = new MailQueueWorker.BatchResult(fetched, failed);

            Assert.Equal(expected, MailQueueWorker.ShouldContinueImmediately(batch, batchSize: 10));
        }
    }
}
