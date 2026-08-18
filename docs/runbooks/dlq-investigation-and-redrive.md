# DLQ Investigation and Redrive

Raised by the `reliable-orders-<environment>-DeadLetterQueueNotEmpty` alarm, which fires on a single
visible message. Anything on the dead-letter queue exhausted `maxReceiveCount` receives, so it has
already been retried and has already failed the same way each time.

Do not redrive before finishing the Diagnose step. A message that dead-lettered for a permanent
reason will dead-letter again, and the redrive costs another full retry cycle for every message
moved with it.

## Inspect

The queue URLs are stack outputs, so read them rather than composing them.

```bash
STACK=ReliableOrders-dev
DLQ=$(aws cloudformation describe-stacks --stack-name "$STACK" \
  --query "Stacks[0].Outputs[?OutputKey=='DeadLetterQueueUrl'].OutputValue" --output text)
SOURCE=$(aws cloudformation describe-stacks --stack-name "$STACK" \
  --query "Stacks[0].Outputs[?OutputKey=='SourceQueueUrl'].OutputValue" --output text)
```

Read messages without consuming them. A visibility timeout of zero leaves them where they are, so
the same message can be read again and the queue depth does not change under you.

```bash
aws sqs receive-message --queue-url "$DLQ" \
  --max-number-of-messages 10 --visibility-timeout 0 \
  --attribute-names All --message-attribute-names All
```

Record the `MessageId` of each. That value is what ties a dead-lettered message to the log lines
written while it was failing.

## Diagnose

The log group is created by the stack rather than named by convention, so ask the function for it
instead of assuming a path.

```bash
FUNCTION=$(aws cloudformation describe-stacks --stack-name "$STACK" \
  --query "Stacks[0].Outputs[?OutputKey=='OrderProcessorFunctionName'].OutputValue" --output text)
GROUP=$(aws lambda get-function-configuration --function-name "$FUNCTION" \
  --query 'LoggingConfig.LogGroup' --output text)
```

Every processing log line carries `SqsMessageId`, so one query returns the whole history of a
message across all of its receives.

```text
fields @timestamp, Outcome, Reason, FailedRules, ApproximateReceiveCount, EventId, OrderId
| filter SqsMessageId = "<message id>"
| sort @timestamp asc
```

`Reason` is the field that says what to do next. It is prefixed by classification, and the prefix
decides the branch.

| Prefix | Meaning | Where to go |
| --- | --- | --- |
| `body.` or `json.` or `schema-version.` | The message never parsed | Repair, publisher side |
| `conflict.` | Two payloads claim one identity | [Idempotency Conflict](idempotency-conflict.md) |
| `permanent.` | The store will never accept this request | Repair, this service |
| `transient.` | Retryable, and it ran out of retries | Repair, capacity or dependency |

A message with no log line at all never reached the handler. Check the function's own errors and the
`reliable-orders-<environment>-FunctionThrottled` alarm before treating it as a message defect.

### Parse failures

`body.empty`, `body.too-large`, `json.invalid`, `json.root-not-object`, `json.field-type-mismatch`,
`schema-version.unreadable` and `schema-version.unsupported` are all publisher defects. The message
cannot be made valid by anything this service does, and redriving it unchanged will dead-letter it
again. `schema-version.unsupported` is the one worth separating, because it can also mean this
service is behind a contract the publisher has already moved to.

### Permanent store failures

`permanent.table-not-found` and `permanent.access-denied` are faults in this service or its
deployment, not in the message. Both mean every message failed, not this one, so expect the queue to
be full rather than holding a single item. Fix the deployment first and the messages are then
redrivable unchanged.

`permanent.malformed-request` and `permanent.item-too-large` are message-shaped and will not survive
a redrive.

### Transient failures that ran out

`transient.throttled`, `transient.service-unavailable`, `transient.transaction-conflict`,
`transient.conflicting-item-missing` and `transient.unreadable-cancellation` are all retryable. A
message here means the retries were exhausted while the condition persisted, so the question is
whether the condition has cleared. Check the `reliable-orders-<environment>-TableThrottlingOrErrors`
alarm and the DynamoDB consumed capacity widget on the dashboard before redriving.

## Repair

Repair the cause, not the message. Redriving is the last step, and it is safe only once the reason
for the original failure no longer holds.

| Cause | Repair |
| --- | --- |
| Publisher defect | The publisher republishes. The order arrives under a new `eventId` and is written normally. |
| Missing IAM action or wrong table | Redeploy the stack with the fix, then redrive unchanged. |
| Throttling or a dependency outage | Wait for the condition to clear, then redrive unchanged. |
| Conflict | Do not redrive. Follow [Idempotency Conflict](idempotency-conflict.md). |

A dead-lettered message that the publisher has already republished must not be redriven. The
republish carries a new `eventId` and the same `orderId`, so the order is already written. Redriving
the original adds a second event for an order that exists, which the order-scope hash will refuse.

## Redrive

Redrive moves messages back to the source queue. The dead-letter queue's redrive allow policy names
the source queue, so this is the only destination it accepts.

```bash
DLQ_ARN=$(aws sqs get-queue-attributes --queue-url "$DLQ" \
  --attribute-names QueueArn --query 'Attributes.QueueArn' --output text)
SOURCE_ARN=$(aws sqs get-queue-attributes --queue-url "$SOURCE" \
  --attribute-names QueueArn --query 'Attributes.QueueArn' --output text)

aws sqs start-message-move-task \
  --source-arn "$DLQ_ARN" --destination-arn "$SOURCE_ARN" \
  --max-number-of-messages-per-second 10
```

Rate-limit the move. An unbounded redrive of a large queue arrives as a burst, and the function's
reserved concurrency turns that into throttling, which is how a redrive of transient failures
recreates the condition that caused them.

Track the task and cancel it if the failures return.

```bash
aws sqs list-message-move-tasks --source-arn "$DLQ_ARN" --max-results 1
aws sqs cancel-message-move-task --task-handle "<task handle>"
```

## Verify

The redrive worked when all three of the following hold.

- The dead-letter queue's visible message count is zero and the
  `reliable-orders-<environment>-DeadLetterQueueNotEmpty` alarm has returned to OK.
- `OrdersProcessed` and `DuplicateEvents` on the dashboard account for the redriven messages. A
  redriven message that was already written arrives as a duplicate, which is a success.
- No new messages appeared on the dead-letter queue during the move.

A redrive that returns messages to the dead-letter queue means the Diagnose step reached the wrong
conclusion. Do not repeat it. Start again from Inspect with the new log lines, which now carry a
higher `ApproximateReceiveCount` and a second failure to compare against the first.
