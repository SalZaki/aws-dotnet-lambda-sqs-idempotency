# Processing Backlog

Raised by one of four alarms, and which one fired decides where to start.

| Alarm | What it means |
| --- | --- |
| `reliable-orders-<environment>-SourceQueueBacklog` | The oldest message is older than the configured threshold. Work is arriving faster than it is leaving, or something is stuck. |
| `reliable-orders-<environment>-no-progress` | Messages are available and nothing at all is being processed. The most serious of the four. |
| `reliable-orders-<environment>-FunctionThrottled` | The function was throttled in every one of the last few minutes. |
| `reliable-orders-<environment>-DeadlineDeferrals` | Records were deferred because invocation time ran low. A tuning signal rather than a fault. |

A backlog is not by itself a failure. The queue exists to absorb bursts, and depth that is falling
is the system working. What matters is the direction of travel and whether anything is completing.

## Inspect

Establish whether work is completing before anything else. The no-progress alarm answers this
directly, and it is a composite over two conditions that are each healthy alone.

```bash
STACK=ReliableOrders-dev
SOURCE=$(aws cloudformation describe-stacks --stack-name "$STACK" \
  --query "Stacks[0].Outputs[?OutputKey=='SourceQueueUrl'].OutputValue" --output text)

aws sqs get-queue-attributes --queue-url "$SOURCE" --attribute-names \
  ApproximateNumberOfMessages ApproximateNumberOfMessagesNotVisible \
  ApproximateAgeOfOldestMessage
```

Read the three together.

- Depth high and in-flight zero means nothing is being picked up. Go to Nothing is being processed.
- Depth high and in-flight at the concurrency ceiling means the consumer is saturated. Go to
  Throughput is the limit.
- Depth falling and age falling means the burst is draining. Record the peak and close the incident.

The dashboard carries the same three as widgets, plus the processed and duplicate counts, which is
the faster read when the incident is already open.

## Diagnose

### Nothing is being processed

The no-progress alarm fires only when messages are available and the sum of `OrdersProcessed` and
`DuplicateEvents` is zero across the window. The sum matters, because a replay storm is processed
correctly while new orders stay flat, so a flat `OrdersProcessed` alone is not a stall.

Work through these in order.

- Check the function's errors and throttles on the dashboard. A function failing on every invocation
  produces errors rather than silence.
- Check whether the event source mapping is enabled. A disabled mapping is the one cause that
  produces exactly this signature, with no errors and no invocations at all.

  ```bash
  FUNCTION=$(aws cloudformation describe-stacks --stack-name "$STACK" \
    --query "Stacks[0].Outputs[?OutputKey=='OrderProcessorFunctionName'].OutputValue" --output text)

  aws lambda list-event-source-mappings --function-name "$FUNCTION" \
    --query 'EventSourceMappings[].{State:State,LastError:LastProcessingResult}'
  ```

- Check `reliable-orders-<environment>-TableThrottlingOrErrors`. A store that refuses every write
  turns every record into a transient failure, which returns the whole batch and leaves the queue
  depth flat while the function runs continuously.
- Check the function's logs for a startup failure. A configuration error that throws in the
  composition root fails every invocation before any record is read, so no processing log line is
  written at all.

### Throughput is the limit

Throttling means the event source is asking for more concurrent executions than the function may
use. Reserved concurrency is the ceiling and the event source's maximum concurrency is checked
against it at synthesis, so a throttle here is load rather than misconfiguration.

Confirm which of the two limits is binding.

```bash
aws lambda get-function-concurrency --function-name "$FUNCTION"
```

Compare the concurrent-executions widget against that number. Sitting at the ceiling for the whole
window is saturation. Spiking to it briefly is a burst the queue is absorbing, which is what the
queue is for.

### Records are being deferred

A deferral means the invocation ran out of time to start work it had already received, so the record
was returned rather than attempted. It is reported by `DeadlineDeferrals` and logged with
`OverrunMs` against the `DeadlineDeferred` outcome.

```text
fields @timestamp, OverrunMs, RecordCount, FailureCount, LambdaRequestId
| filter Outcome = "DeadlineDeferred"
| sort @timestamp desc
```

Deferrals are a tuning signal. The batch is too large for the invocation to finish, or the deadline
margin leaves too little room. Either the batch size comes down or the function timeout goes up, and
`OverrunMs` says how far past the deadline the work was running, which is the number that decides
which.

Deferred records are returned to the queue and redelivered, so a deferral costs latency rather than
data. A sustained deferral rate does become a backlog, because each deferred record is received
again and consumes a receive against `maxReceiveCount`.

## Repair

| Diagnosis | Repair |
| --- | --- |
| Event source mapping disabled | Re-enable it. Establish why it was disabled before doing so. |
| Startup failure in the function | Fix the configuration and redeploy. Every record is still on the queue and is redelivered. |
| Store throttling | Follow the throttling branch below rather than raising concurrency, which makes it worse. |
| Saturation under genuine load | Raise reserved concurrency and the event source maximum together. The configuration refuses a maximum above the reserved value. |
| Deferrals | Lower the batch size or raise the function timeout. Re-derive the visibility timeout, which is computed from the function timeout. |

Raising concurrency while DynamoDB is throttling makes the incident worse. More concurrent
executions issue more transactional writes against the same throttled table, so throughput falls
while every record still consumes a receive. Let the table recover first.

Nothing in this runbook requires touching messages. The queue redelivers everything it holds once
the consumer is healthy, and a backlog drains without operator intervention.

## Verify

The backlog is resolved when all of the following hold.

- `ApproximateAgeOfOldestMessage` is falling and the
  `reliable-orders-<environment>-SourceQueueBacklog` alarm has returned to OK.
- `OrdersProcessed` and `DuplicateEvents` are non-zero across a full window, so the no-progress
  condition cannot be silently true.
- The dead-letter queue has not grown. A backlog that drains into the dead-letter queue was a
  failure incident rather than a capacity one, and continues at
  [DLQ Investigation and Redrive](dlq-investigation-and-redrive.md).

A configuration change made during the incident, such as a raised concurrency or a lowered batch
size, belongs in `EnvironmentConfig` rather than left applied to the deployed resources. A console
change is reverted by the next deployment, silently, at whatever time that deployment happens.
