import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { DaemonApiClient, daemonApi } from './client';
import { DaemonApiError } from './http';
import { browserSession } from './session.svelte';
import type { Schema } from './types';
import type {
  ChatTargetSnapshotDto,
  StateSnapshotDto,
  StateUpdateBatchDto,
  UserBrowseDto,
  WireInteger,
} from './types';
import { wireInteger } from './types';
import { DaemonStateReducer, type ReplicatedDaemonState, type DaemonStateChanges } from './state.svelte';
export { DaemonStateReducer } from './state.svelte';
export type { ReplicatedDaemonState } from './state.svelte';

const expectedLiveProtocolVersion = 18 satisfies Schema<'LiveProtocolContractVersion'>;
const stateUpdateBatchMessage = 'stateUpdateBatch' satisfies Schema<'LiveServerMessage'>;
const subscribeAllMethod = 'SubscribeAll' satisfies Schema<'LiveClientInvocation'>;
const unsubscribeAllMethod = 'UnsubscribeAll' satisfies Schema<'LiveClientInvocation'>;
const subscribeChatMethod = 'SubscribeChat' satisfies Schema<'LiveClientInvocation'>;
const unsubscribeChatMethod = 'UnsubscribeChat' satisfies Schema<'LiveClientInvocation'>;
const subscribeUserBrowseMethod = 'SubscribeUserBrowse' satisfies Schema<'LiveClientInvocation'>;
const unsubscribeUserBrowseMethod = 'UnsubscribeUserBrowse' satisfies Schema<'LiveClientInvocation'>;
const observeSearchViewMethod = 'ObserveSearchView' satisfies Schema<'LiveClientInvocation'>;
const observeTransferTimelineMethod = 'ObserveTransferTimeline' satisfies Schema<'LiveClientInvocation'>;
const observeJobsMethod = 'ObserveJobs' satisfies Schema<'LiveClientInvocation'>;
const chatMessageTailSize = 100 satisfies Schema<'ChatLiveMessageTailSize'>;

export type LiveConnectionStatus = 'offline' | 'connecting' | 'connected' | 'reconnecting' | 'failed';
export type BatchApplyStatus = 'applied' | 'stale' | 'recovery-required';

export class ChatStateReducer {
  private target: ChatTargetSnapshotDto | null = null;
  private epoch: string | null = null;
  private sequence: WireInteger = 0;

  snapshot(): ChatTargetSnapshotDto | null { return this.target ? { ...this.target, messages: [...this.target.messages] } : null; }

  applySnapshot(snapshot: StateSnapshotDto): ChatTargetSnapshotDto {
    if (snapshot.scope.kind !== 'chatConversation' && snapshot.scope.kind !== 'chatRoom') throw new Error('Expected a chat-scoped live snapshot.');
    if (!snapshot.chatTarget) throw new Error('The chat snapshot did not contain its target.');
    this.target = snapshot.chatTarget;
    this.epoch = snapshot.position.epoch;
    this.sequence = snapshot.position.sequence;
    return this.snapshot()!;
  }

  applyBatch(batch: StateUpdateBatchDto, observe?: (messages: ChatTargetSnapshotDto['messages']) => void): BatchApplyStatus {
    if (batch.scope.kind !== 'chatConversation' && batch.scope.kind !== 'chatRoom') throw new Error('Expected a chat-scoped live batch.');
    if (!this.target || this.epoch !== batch.epoch) return 'recovery-required';
    if (!newer(batch.sequence, this.sequence)) return 'stale';
    if (wireInteger(batch.previousSequence) > wireInteger(this.sequence)) return 'recovery-required';
    const delta = batch.state.chatTargets?.find((item) => item.targetId === this.target?.targetId);
    if (delta) {
      const messages = new Map<string, ChatTargetSnapshotDto['messages'][number]>();
      if (!delta.replaceMessages) for (const message of this.target.messages) messages.set(message.messageId, message);
      for (const message of delta.messages ?? []) messages.set(message.messageId, message);
      const truncated = messages.size > chatMessageTailSize;
      const tail = [...messages.values()]
        .sort((left, right) => wireInteger(right.sequence) > wireInteger(left.sequence) ? 1 : wireInteger(right.sequence) < wireInteger(left.sequence) ? -1 : 0)
        .slice(0, chatMessageTailSize)
        .reverse();
      this.target = {
        ...this.target,
        conversation: delta.conversation ?? this.target.conversation,
        room: delta.room ?? this.target.room,
        messages: tail,
        hasEarlierMessages: delta.hasEarlierMessages ?? (delta.replaceMessages ? truncated : this.target.hasEarlierMessages || truncated),
      };
    }
    this.sequence = batch.sequence;
    observe?.(delta?.messages ?? []);
    return 'applied';
  }
}

export class UserBrowseStateReducer {
  private browse: UserBrowseDto | null = null;
  private epoch: string | null = null;
  private sequence: WireInteger = 0;

  snapshot(): UserBrowseDto | null { return this.browse ? { ...this.browse } : null; }

  applySnapshot(snapshot: StateSnapshotDto): UserBrowseDto {
    if (snapshot.scope.kind !== 'userBrowse' || !snapshot.userBrowse
      || snapshot.scope.userBrowseId !== snapshot.userBrowse.browseId) {
      throw new Error('Expected a browse-scoped snapshot with its matching resource.');
    }
    this.browse = snapshot.userBrowse;
    this.epoch = snapshot.position.epoch;
    this.sequence = snapshot.position.sequence;
    return this.snapshot()!;
  }

  applyBatch(batch: StateUpdateBatchDto): BatchApplyStatus {
    if (batch.scope.kind !== 'userBrowse') throw new Error('Expected a browse-scoped live batch.');
    if (!this.browse || this.epoch !== batch.epoch) return 'recovery-required';
    if (batch.scope.userBrowseId !== this.browse.browseId
      || batch.state.userBrowse && batch.state.userBrowse.browseId !== this.browse.browseId) {
      throw new Error('The browse update belongs to a different resource.');
    }
    if (!newer(batch.sequence, this.sequence)) return 'stale';
    if (wireInteger(batch.previousSequence) > wireInteger(this.sequence)) return 'recovery-required';
    const next = batch.state.userBrowse;
    if (next && newer(next.revision, this.browse.revision)) this.browse = next;
    this.sequence = batch.sequence;
    return 'applied';
  }
}

function newer(next: WireInteger, current: WireInteger): boolean {
  return wireInteger(next) > wireInteger(current);
}

type StateListener = (state: ReplicatedDaemonState, changes: DaemonStateChanges) => void;
type StatusListener = (status: LiveConnectionStatus, error: Error | null) => void;
type ChatListener = (target: ChatTargetSnapshotDto, observedMessages: ChatTargetSnapshotDto['messages'], snapshotRefresh: boolean) => void;

interface ScopedSessionState {
  scope: Schema<'StateStreamScopeDto'>;
  buffering: boolean;
  buffered: StateUpdateBatchDto[];
  recovering: Promise<void> | null;
  starting: Promise<void> | null;
}

interface ChatSession extends ScopedSessionState {
  kind: 'chat';
  reducer: ChatStateReducer;
  listeners: Set<ChatListener>;
}

interface BrowseSession extends ScopedSessionState {
  kind: 'browse';
  reducer: UserBrowseStateReducer;
  listeners: Map<(browse: UserBrowseDto) => void, (error: Error) => void>;
}

type ScopedSession = ChatSession | BrowseSession;

export class DaemonLiveClient {
  readonly reducer = new DaemonStateReducer();
  private connection: HubConnection | null = null;
  private stateListeners = new Set<StateListener>();
  private statusListeners = new Set<StatusListener>();
  private buffering = true;
  private buffered: StateUpdateBatchDto[] = [];
  private recovering: Promise<void> | null = null;
  private stopped = false;
  private lifecycleId = 0;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private retryDelayMs = 1_000;
  private scopedSessions = new Map<string, ScopedSession>();

  constructor(private readonly api: DaemonApiClient = daemonApi, private readonly detail: Schema<'DaemonStateDetail'> = 'Overview') {}

  observeSearchView(viewId: string, next: (summary: Schema<'SearchViewSummaryDto'>) => void,
    error: (error: Error) => void, complete: () => void): () => void {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Connected)
      throw new Error('The daemon live connection is not ready.');
    const subscription = connection.stream<Schema<'SearchViewSummaryDto'>>(observeSearchViewMethod, viewId)
      .subscribe({ next, error: (failure) => error(asError(failure)), complete });
    return () => subscription.dispose();
  }

  onState(listener: StateListener): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }

  observeTransferTimeline(scope: Schema<'TransferTimelineScopeDto'>, next: (update: Schema<'TransferTimelineUpdateDto'>) => void,
    error: (error: Error) => void): () => void {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Connected)
      throw new Error('The daemon live connection is not ready.');
    const subscription = connection.stream<Schema<'TransferTimelineUpdateDto'>>(observeTransferTimelineMethod,
      scope)
      .subscribe({ next, error: (failure) => error(asError(failure)),
        complete: () => error(new Error('Transfer updates stopped. Retry to reconnect.')) });
    return () => subscription.dispose();
  }

  observeJobList(scope: Schema<'JobListScopeDto'>, next: (update: Schema<'JobListUpdateDto'>) => void,
    error: (error: Error) => void): () => void {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Connected)
      throw new Error('The daemon live connection is not ready.');
    const method = 'ObserveJobList' satisfies Schema<'LiveClientInvocation'>;
    const subscription = connection.stream<Schema<'JobListUpdateDto'>>(method, scope)
      .subscribe({ next, error: cause => error(asError(cause)),
        complete: () => error(new Error('Job list updates stopped. Retry to reconnect.')) });
    return () => subscription.dispose();
  }

  observeJobs(jobId: string, jobIds: string[], next: (update: Schema<'JobObservationUpdateDto'>) => void,
    error: (error: Error) => void): () => void {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Connected)
      throw new Error('The daemon live connection is not ready.');
    const subscription = connection.stream<Schema<'JobObservationUpdateDto'>>(observeJobsMethod, jobId, jobIds)
      .subscribe({ next, error: failure => error(asError(failure)),
        complete: () => error(new Error('Live job updates stopped. Retry to reconnect.')) });
    return () => subscription.dispose();
  }

  onStatus(listener: StatusListener): () => void {
    this.statusListeners.add(listener);
    return () => this.statusListeners.delete(listener);
  }

  async startDaemon(): Promise<void> {
    if (this.connection) return;
    const lifecycleId = ++this.lifecycleId;
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    this.stopped = false;
    this.emitStatus('connecting');
    let info: Awaited<ReturnType<DaemonApiClient['getServerInfo']>>;
    try {
      info = await this.api.getServerInfo();
    } catch (error) {
      if (this.stopped || lifecycleId !== this.lifecycleId) return;
      this.emitStatus(error instanceof DaemonApiError ? 'failed' : 'offline', asError(error));
      this.scheduleRetry();
      throw error;
    }
    if (this.stopped || lifecycleId !== this.lifecycleId) return;
    if (Number(info.liveProtocolVersion) !== expectedLiveProtocolVersion) {
      const error = new Error(
        `Incompatible live protocol: WebUI requires ${expectedLiveProtocolVersion}, daemon exposes ${info.liveProtocolVersion}.`,
      );
      this.emitStatus('failed', error);
      throw error;
    }

    const connection = new HubConnectionBuilder()
      .withUrl(`/api/events?liveProtocol=${expectedLiveProtocolVersion}`)
      .withAutomaticReconnect()
      .configureLogging(import.meta.env.DEV ? LogLevel.Warning : LogLevel.Error)
      .build();
    this.connection = connection;
    connection.on(stateUpdateBatchMessage, (batch: StateUpdateBatchDto) => {
      if (!this.stopped && this.connection === connection) this.receive(batch);
    });
    connection.onreconnecting((error) => {
      if (this.stopped || this.connection !== connection) return;
      if (browserSession.data?.authenticationEnabled) void browserSession.refresh().catch(() => {});
      this.resetRecovery();
      this.emitStatus('reconnecting', error ?? null);
    });
    connection.onreconnected(async () => {
      if (this.stopped || this.connection !== connection) return;
      try {
        await connection.invoke(subscribeAllMethod, this.detail);
        await this.recover();
        if (this.stopped || this.connection !== connection) return;
        await this.resumeScopes(connection);
        if (!this.stopped && this.connection === connection) this.emitStatus('connected');
      } catch (error) {
        if (!this.stopped && this.connection === connection) {
          this.emitStatus('failed', asError(error));
          await connection.stop();
        }
      }
    });
    connection.onclose((error) => {
      if (this.connection !== connection) return;
      if (browserSession.data?.authenticationEnabled) void browserSession.refresh().catch(() => {});
      this.resetRecovery();
      this.connection = null;
      if (!this.stopped) {
        this.emitStatus(error ? 'failed' : 'offline', error ?? null);
        this.scheduleRetry();
      }
    });

    try {
      this.buffering = true;
      await connection.start();
      if (this.stopped || lifecycleId !== this.lifecycleId) {
        await connection.stop();
        return;
      }
      await connection.invoke(subscribeAllMethod, this.detail);
      if (this.stopped || lifecycleId !== this.lifecycleId) return;
      await this.recover();
      await this.resumeScopes(connection);
      if (this.stopped || lifecycleId !== this.lifecycleId) {
        await connection.stop();
        return;
      }
      this.retryDelayMs = 1_000;
      this.emitStatus('connected');
    } catch (error) {
      if (this.stopped || lifecycleId !== this.lifecycleId) return;
      this.emitStatus('failed', asError(error));
      await connection.stop();
      this.connection = null;
      this.scheduleRetry();
      throw error;
    }
  }

  async stop(): Promise<void> {
    this.stopped = true;
    this.resetRecovery();
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    const connection = this.connection;
    this.connection = null;
    if (connection?.state === HubConnectionState.Connected) {
      for (const session of this.scopedSessions.values()) {
        await this.unsubscribeScope(connection, session).catch(() => undefined);
      }
      await connection.invoke(unsubscribeAllMethod).catch(() => undefined);
    }
    await connection?.stop();
    this.emitStatus('offline');
  }

  private resetRecovery(): void {
    // Reads started before a connection loss cannot establish the rejoined
    // stream's position. Keep active listeners, but require fresh snapshots.
    this.lifecycleId++;
    this.recovering = null;
    this.buffering = true;
    this.buffered = [];
    for (const [key, session] of this.scopedSessions) {
      session.recovering = null;
      session.starting = null;
      session.buffering = true;
      session.buffered = [];
      if (!session.listeners.size) this.scopedSessions.delete(key);
    }
  }

  private scheduleRetry(): void {
    if (this.stopped || this.retryTimer) return;
    const delay = this.retryDelayMs;
    this.retryDelayMs = Math.min(this.retryDelayMs * 2, 30_000);
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      if (!this.stopped) void this.startDaemon().catch(() => undefined);
    }, delay);
  }

  async subscribeChat(kind: 'conversation' | 'room', targetId: string, listener: ChatListener): Promise<() => Promise<void>> {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Connected) throw new Error('The daemon live connection is not ready.');
    const scope: Schema<'StateStreamScopeDto'> = {
      kind: kind === 'conversation' ? 'chatConversation' : 'chatRoom',
      chatTargetId: targetId,
    };
    const key = scopedStreamKey(scope);
    let session = this.scopedSessions.get(key);
    if (session && session.kind !== 'chat') throw new Error('Expected a chat stream.');
    if (!session) {
      session = { kind: 'chat', scope, reducer: new ChatStateReducer(), listeners: new Set(), buffering: true, buffered: [], recovering: null, starting: null };
      this.scopedSessions.set(key, session);
    }
    if (!session.reducer.snapshot() || session.starting) await this.activateScope(connection, session);
    if (this.stopped || this.connection !== connection || this.scopedSessions.get(key) !== session)
      throw new Error('The live subscription was interrupted.');
    session.listeners.add(listener);
    const current = session.reducer.snapshot();
    if (current) listener(current, current.messages, true);
    const active = session;
    return this.releaseScope(active, () => active.listeners.delete(listener));
  }

  async subscribeUserBrowse(browseId: string, listener: (browse: UserBrowseDto) => void,
    onError: (error: Error) => void): Promise<() => Promise<void>> {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Connected) throw new Error('The daemon live connection is not ready.');
    const scope: Schema<'StateStreamScopeDto'> = { kind: 'userBrowse', userBrowseId: browseId };
    const key = scopedStreamKey(scope);
    let session = this.scopedSessions.get(key);
    if (session && session.kind !== 'browse') throw new Error('Expected a browse stream.');
    if (!session) {
      session = { kind: 'browse', scope, reducer: new UserBrowseStateReducer(), listeners: new Map(), buffering: true, buffered: [], recovering: null, starting: null };
      this.scopedSessions.set(key, session);
    }
    if (!session.reducer.snapshot() || session.starting) await this.activateScope(connection, session);
    if (this.stopped || this.connection !== connection || this.scopedSessions.get(key) !== session)
      throw new Error('The live subscription was interrupted.');
    session.listeners.set(listener, onError);
    const current = session.reducer.snapshot();
    if (current) listener(current);
    const active = session;
    return this.releaseScope(active, () => active.listeners.delete(listener));
  }

  private subscribeScope(connection: HubConnection, session: ScopedSession): Promise<void> {
    return session.kind === 'chat'
      ? connection.invoke(subscribeChatMethod, session.scope)
      : connection.invoke(subscribeUserBrowseMethod, session.scope.userBrowseId);
  }

  private unsubscribeScope(connection: HubConnection, session: ScopedSession): Promise<void> {
    return session.kind === 'chat'
      ? connection.invoke(unsubscribeChatMethod, session.scope)
      : connection.invoke(unsubscribeUserBrowseMethod, session.scope.userBrowseId);
  }

  private activateScope(connection: HubConnection, session: ScopedSession): Promise<void> {
    if (session.starting) return session.starting;
    const lifecycleId = this.lifecycleId;
    session.buffering = true;
    const starting = (async () => {
      await this.subscribeScope(connection, session);
      if (this.stopped || this.lifecycleId !== lifecycleId || this.connection !== connection) return;
      await this.recoverScope(session);
    })().catch(async error => {
      if (this.stopped || this.lifecycleId !== lifecycleId || this.connection !== connection) return;
      const key = scopedStreamKey(session.scope);
      if (this.scopedSessions.get(key) === session) this.scopedSessions.delete(key);
      await this.unsubscribeScope(connection, session).catch(() => undefined);
      throw error;
    }).finally(() => { if (session.starting === starting) session.starting = null; });
    session.starting = starting;
    return starting;
  }

  private async resumeScopes(connection: HubConnection): Promise<void> {
    for (const session of this.scopedSessions.values()) {
      if (this.stopped || this.connection !== connection) return;
      try { await this.activateScope(connection, session); }
      catch (error) {
        if (session.kind === 'chat') throw error;
        for (const onError of session.listeners.values()) onError(asError(error));
      }
    }
  }

  private releaseScope(session: ScopedSession, removeListener: () => void): () => Promise<void> {
    return async () => {
      const key = scopedStreamKey(session.scope);
      if (this.scopedSessions.get(key) !== session) return;
      removeListener();
      if (session.listeners.size) return;
      this.scopedSessions.delete(key);
      if (this.connection?.state === HubConnectionState.Connected) {
        await this.unsubscribeScope(this.connection, session).catch(() => undefined);
      }
    };
  }

  private receive(batch: StateUpdateBatchDto): void {
    if (batch.scope.kind === 'chatConversation' || batch.scope.kind === 'chatRoom' || batch.scope.kind === 'userBrowse') {
      const session = this.scopedSessions.get(scopedStreamKey(batch.scope));
      if (!session) return;
      if (session.buffering) {
        session.buffered.push(batch);
        return;
      }
      let status: BatchApplyStatus;
      try {
        status = session.kind === 'chat'
          ? session.reducer.applyBatch(batch, messages => this.emitChat(session, messages, false))
          : session.reducer.applyBatch(batch);
      } catch (error) { this.emitScopeError(session, error); return; }
      if (status === 'applied' && session.kind === 'browse') this.emitBrowse(session);
      if (status === 'recovery-required') void this.recoverScope(session).catch(error => this.emitScopeError(session, error));
      return;
    }
    if (batch.scope.kind !== 'daemon') return;
    if (this.buffering) {
      this.buffered.push(batch);
      return;
    }
    const status = this.reducer.applyBatch(batch);
    if (status === 'applied') {
      this.emitState();
    }
    else if (status === 'recovery-required') void this.recover().catch((error) => {
      this.emitStatus('failed', asError(error));
    });
  }

  private recoverScope(session: ScopedSession): Promise<void> {
    if (session.recovering) return session.recovering;
    const lifecycleId = this.lifecycleId;
    session.buffering = true;
    const recovery = (async () => {
      const snapshot = session.kind === 'browse'
        ? await this.api.getUserBrowseSnapshot(session.scope.userBrowseId!)
        : session.scope.kind === 'chatConversation'
          ? await this.api.getConversationSnapshot(session.scope.chatTargetId!)
          : await this.api.getRoomSnapshot(session.scope.chatTargetId!);
      if (this.stopped || lifecycleId !== this.lifecycleId) return;
      if (scopedStreamKey(snapshot.scope) !== scopedStreamKey(session.scope))
        throw new Error('The scoped snapshot belongs to a different resource.');
      session.reducer.applySnapshot(snapshot);
      const buffered = session.buffered;
      session.buffered = [];
      const observed = [...(snapshot.chatTarget?.messages ?? [])];
      for (const batch of buffered) {
        const status = session.kind === 'chat'
          ? session.reducer.applyBatch(batch, messages => { for (const message of messages) observed.push(message); })
          : session.reducer.applyBatch(batch);
        if (status === 'recovery-required') throw new Error('Scoped updates remained discontinuous after snapshot recovery.');
      }
      // Publication ends recovery. An observer may immediately expose another
      // gap; do not let the old promise's cleanup erase that newer recovery.
      session.recovering = null;
      session.buffering = false;
      if (session.kind === 'chat') this.emitChat(session, observed, true);
      else this.emitBrowse(session);
    })().catch(error => {
      if (!this.stopped && lifecycleId === this.lifecycleId) throw error;
    }).finally(() => { if (session.recovering === recovery) session.recovering = null; });
    session.recovering = recovery;
    return recovery;
  }

  private emitChat(session: ChatSession, observed: ChatTargetSnapshotDto['messages'], snapshotRefresh: boolean): void {
    const target = session.reducer.snapshot();
    if (!target) return;
    for (const listener of session.listeners) listener(target, observed, snapshotRefresh);
  }

  private emitBrowse(session: BrowseSession): void {
    const browse = session.reducer.snapshot();
    if (browse) for (const listener of session.listeners.keys()) listener(browse);
  }

  private emitScopeError(session: ScopedSession, error: unknown): void {
    const key = scopedStreamKey(session.scope);
    if (this.scopedSessions.get(key) !== session) return;
    if (session.kind === 'browse') {
      this.scopedSessions.delete(key);
      session.buffered = [];
      if (this.connection?.state === HubConnectionState.Connected)
        void this.unsubscribeScope(this.connection, session).catch(() => undefined);
      for (const onError of session.listeners.values()) onError(asError(error));
    }
    else this.emitStatus('failed', asError(error));
  }

  private recover(): Promise<void> {
    if (this.recovering) return this.recovering;
    const lifecycleId = this.lifecycleId;
    this.buffering = true;
    const recovery = (async () => {
      const snapshot = await this.api.getDaemonSnapshot(undefined, this.detail);
      if (this.stopped || lifecycleId !== this.lifecycleId) return;
      this.reducer.applySnapshot(snapshot);
      const buffered = this.buffered;
      this.buffered = [];
      for (const batch of buffered) {
        const status = this.reducer.applyBatch(batch);
        if (status === 'recovery-required') {
          throw new Error('Live updates remained discontinuous after snapshot recovery.');
        }
      }
      this.recovering = null;
      this.buffering = false;
      this.emitState();
    })().catch(error => {
      if (!this.stopped && lifecycleId === this.lifecycleId) throw error;
    }).finally(() => {
      if (this.recovering === recovery) this.recovering = null;
    });
    this.recovering = recovery;
    return recovery;
  }

  private emitState(): void {
    const state = this.reducer.state;
    const changes = this.reducer.takeChanges();
    for (const listener of this.stateListeners) listener(state, changes);
  }

  private emitStatus(status: LiveConnectionStatus, error: Error | null = null): void {
    for (const listener of this.statusListeners) listener(status, error);
  }
}

function asError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

function scopedStreamKey(scope: Pick<Schema<'StateStreamScopeDto'>, 'kind' | 'chatTargetId' | 'userBrowseId'>): string {
  return `${scope.kind}:${scope.chatTargetId ?? scope.userBrowseId ?? ''}`;
}
