# Overview

## Executive Summary

This project implements an event-driven order processor in .NET 10. An Amazon SQS standard queue
invokes an AWS Lambda function with batches of order events. The worker validates each event,
prevents duplicate business effects, stores the order atomically in DynamoDB, reports per-record
failures, and allows repeatedly failing messages to move to a dead-letter queue.

The project deliberately models the delivery contract accurately.

- Amazon SQS and Lambda provide **at-least-once delivery**.
- Duplicate delivery is expected and tested.
- The application provides **idempotent, effectively-once business effects** for DynamoDB order
  creation.
- It does not claim universal end-to-end exactly-once delivery.

The central correctness mechanism is a DynamoDB transaction that writes the order and its
idempotency record as one all-or-nothing operation. This avoids the failure window created by
marking a message as processed before the order has actually been saved.

## Why This Project Is Valuable

This is a compact project, but it exercises a broad set of commercially useful skills.

### AWS skills

- Lambda event source mappings
- SQS batching, visibility timeouts, retries, dead-letter queues, and redrive
- DynamoDB conditional writes, transactions, TTL, on-demand capacity, and point-in-time recovery
- CloudWatch Logs, Embedded Metric Format, dashboards, and alarms
- OpenTelemetry and AWS Distro for OpenTelemetry
- IAM least privilege
- AWS CDK v2 in C#
- Secure GitHub-to-AWS deployments with OpenID Connect
- Cloud-based end-to-end testing

### .NET skills

- .NET 10 and modern C#
- Dependency injection and composition roots
- `System.Text.Json` source generation
- Immutable message contracts
- Validation and explicit error classification
- Cancellation and timeout handling
- AWS SDK for .NET v4
- Unit, integration, architecture, and end-to-end testing
- Central package management and reproducible builds
- Structured logging and custom metrics
- Native AOT benchmarking as an optional extension

### Distributed systems skills

- At-least-once delivery
- Idempotency and duplicate detection
- Atomicity and failure windows
- Poison-message handling
- Partial batch responses
- Backpressure and concurrency control
- Payload versioning
- Correlation and causation
- Operational observability
- Transactional outbox as a future extension

## Project Goals

1. Process order events from an SQS standard queue.
2. Handle duplicate delivery without creating duplicate orders.
3. Atomically persist the order and idempotency record.
4. Return only failed SQS message identifiers in the Lambda batch response.
5. Move repeatedly failing messages to a DLQ.
6. Distinguish successful, duplicate, permanent-failure, and transient-failure outcomes.
7. Define all AWS resources using AWS CDK in C#.
8. Provide secure CI/CD using GitHub Actions and AWS OIDC.
9. Provide structured logs, custom metrics, traces, dashboards, and alarms.
10. Support fast local tests plus authoritative end-to-end tests in a real AWS environment.
11. Be straightforward for another developer to clone, understand, deploy, test, and remove.
12. Demonstrate production engineering decisions without turning a small worker into an
    unnecessarily complex framework.

## Non-Goals

The first production-quality release excludes the following.

- A complete e-commerce platform
- Payment processing
- Inventory reservation
- A web or mobile user interface
- Multi-Region active-active processing
- Guaranteed ordering between orders
- Multiple inbound event sources
- Long-running workflow orchestration
- External side effects such as email or payment calls
- A generic event-processing framework
- Complex single-table DynamoDB modelling
- A Kubernetes or container-orchestration deployment

Basic contract and business validation are in scope. Complex order-domain validation is not.

## Final Positioning

This project should be presented as follows.

> A production-minded reference implementation for idempotent SQS batch processing in .NET on AWS,
with transactional DynamoDB persistence, partial failure handling, secure infrastructure automation,
and full operational telemetry.

That positioning is technically accurate, commercially relevant, and stronger than claiming generic
exactly-once processing.
