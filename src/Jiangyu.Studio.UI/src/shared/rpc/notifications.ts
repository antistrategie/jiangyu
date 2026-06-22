/**
 * Method → payload map for host-pushed notifications. `subscribe` keys on
 * this interface, so every notification method name and payload shape used
 * by the frontend is declared in exactly one schema and a host-side rename
 * surfaces as a compile error at the subscription site instead of a silent
 * `undefined` deep in the UI.
 *
 * Payloads whose DTOs are generator-owned (see ./types) are declared here.
 * A feature that owns a frontend-only payload shape registers it next to
 * its subscription wiring via declaration merging:
 *
 *   declare module "@shared/rpc/notifications" {
 *     interface HostNotificationMap {
 *       myMethod: MyPayload;
 *     }
 *   }
 */
import type {
  AgentRegistryFetchedNotification,
  AgentSessionCreatedNotification,
  CompileFinishedEvent,
  CompileLogEventPayload,
  CompilePhaseEvent,
  CompileProgressEvent,
  CompileStartedEvent,
  CompileStatusEvent,
  DeployFinishedEvent,
  PackageFinishedEvent,
} from "./types";

export type FileChangeKind = "changed" | "deleted";

export interface FileChangedEvent {
  readonly path: string;
  readonly kind: FileChangeKind;
}

export interface HostNotificationMap {
  fileChanged: FileChangedEvent;
  compileStarted: CompileStartedEvent;
  compilePhase: CompilePhaseEvent;
  compileStatus: CompileStatusEvent;
  compileProgress: CompileProgressEvent;
  compileLog: CompileLogEventPayload;
  compileFinished: CompileFinishedEvent;
  packageFinished: PackageFinishedEvent;
  deployFinished: DeployFinishedEvent;
  agentsRegistryFetched: AgentRegistryFetchedNotification;
  agentSessionCreated: AgentSessionCreatedNotification;
}
