<script lang="ts">
  import LoadingIndicator from '../../components/LoadingIndicator.svelte';
  import { randomUUID } from '../../app/uuid';
  import { onMount, tick } from 'svelte';
  import type { ChatMessageDto, ChatRoomSummaryDto, ChatTargetSnapshotDto, ConversationSummaryDto, Schema, UserPresenceObservationDto } from '../../api/types';
  import { wireInteger } from '../../api/types';
  import type { DaemonLiveClient, LiveConnectionStatus } from '../../api/live';
  import type { UserLinkActions } from '../../app/navigation';
  import { formatCount, userInitials } from '../../app/format';
  import { relativeTime } from '../../app/relative-time';
  import { relativeTimeClock } from '../../app/relative-time-clock';
  import { daemonApi } from '../../api/client';
  import Icon from '../../components/Icon.svelte';
  import LinkifiedText from '../../components/LinkifiedText.svelte';
  import UsernameLink from '../../components/UsernameLink.svelte';
  import LoadMoreButton from '../../components/LoadMoreButton.svelte';
  import MutationStatus, { type MutationState } from '../../components/MutationStatus.svelte';
  import { anchoredMenu } from '../../lib/anchored-menu';
  import { mergeMessages, snapshotNeedsHistory } from './messages';

  type Target = { kind: 'conversation'; id: string } | { kind: 'room'; id: string } | { kind: 'draft'; id: string; username: string };
  type ChatList = 'rooms' | 'conversations';
  const chatLists: ChatList[] = ['conversations', 'rooms'];

  interface Props {
    liveClient: DaemonLiveClient;
    liveStatus: LiveConnectionStatus;
    runtime?: Schema<'ChatRuntimeStateDto'> | null;
    userActions: UserLinkActions;
    initialUsername?: string | null;
  }

  let { liveClient, liveStatus, runtime = null, userActions, initialUsername = null }: Props = $props();
  let rooms = $state<ChatRoomSummaryDto[]>([]);
  let conversations = $state<ConversationSummaryDto[]>([]);
  let roomCursor = $state<string | null>(null);
  let conversationCursor = $state<string | null>(null);
  let listLoading = $state<Record<ChatList, boolean>>({ conversations: true, rooms: true });
  let listsLoading = $derived(listLoading.conversations || listLoading.rooms);
  let listErrors = $state<Partial<Record<ChatList, Error | undefined>>>({});
  const loadedLists = new Set<ChatList>();
  let listsError = $derived.by(() => {
    const failures = chatLists.flatMap(kind => listErrors[kind]
      ? [`${kind === 'rooms' ? 'Rooms' : 'Conversations'}: ${listErrors[kind]!.message}`] : []);
    return failures.length ? new Error(failures.join(' · ')) : null;
  });
  let target = $state<Target | null>(null);
  let targetSnapshot = $state<ChatTargetSnapshotDto | null>(null);
  let historicalMessages = $state<ChatMessageDto[]>([]);
  let messageCursor = $state<string | null>(null);
  let messagesLoading = $state(false);
  let targetError = $state<Error | null>(null);
  let presence = $state<UserPresenceObservationDto | null>(null);
  let presenceError = $state<Error | null>(null);
  let draft = $state('');
  let composerTextarea = $state<HTMLTextAreaElement | null>(null);
  let adding = $state<'room' | 'user' | null>(null);
  let addDraft = $state('');
  let menuOpen = $state(false);
  let messagesElement = $state<HTMLDivElement | null>(null);
  let chatOptionsElement = $state<HTMLDivElement | null>(null);
  let mutation = $state<MutationState>({ phase: 'idle' });
  let activeSubscription: (() => Promise<void>) | null = null;
  let activation = 0;
  let selectionRevision = 0;
  const listRevisions: Record<ChatList, number> = { conversations: 0, rooms: 0 };
  let handledInitialUsername = '';
  let reconciliationRevision = 0;
  let historyBoundary: ChatMessageDto | undefined;
  const markedRead = new Map<string, string>();
  const pendingUnreadLists = new Set<ChatList>();
  let observedUnread: { rooms: number; conversations: number } | null = null;
  let initialUnreadReconciled = false;
  let unreadRefreshTimer: ReturnType<typeof setTimeout> | null = null;
  let unreadRefreshInFlight = false;
  let lastUnreadRefreshAt = -Infinity;
  let disposed = false;
  let wasOffline = false;

  let activeConversation = $derived.by(() => { const current = target; return current?.kind === 'conversation' ? conversations.find((item) => item.conversationId === current.id) ?? targetSnapshot?.conversation ?? null : null; });
  let activeRoom = $derived.by(() => { const current = target; return current?.kind === 'room' ? rooms.find((item) => item.roomId === current.id) ?? targetSnapshot?.room ?? null : null; });
  let activeUsername = $derived(target?.kind === 'draft' ? target.username : activeConversation?.username ?? null);
  let activeUserBlocked = $derived(activeConversation?.privateMessagesBlocked ?? false);
  let presenceLabel = $derived.by(() => {
    if (!presence) return presenceError ? 'presence unavailable' : 'presence unknown';
    if (presence.status.state !== 'available') return 'presence unavailable';
    if (presence.presence === 'unknown') return 'presence unknown';
    const age = relativeTime(presence.observedAtUtc, $relativeTimeClock);
    return age ? `${presence.presence} · observed ${age.toLowerCase()}` : presence.presence;
  });
  let activeMessages = $derived(mergeMessages(historicalMessages, targetSnapshot?.messages ?? []));
  let composerDisabled = $derived(liveStatus !== 'connected' || runtime?.state !== 'Ready' || activeUserBlocked || !target);

  onMount(() => {
    void loadLists(true);
    return () => {
      disposed = true;
      if (unreadRefreshTimer) clearTimeout(unreadRefreshTimer);
      selectionRevision++; activation++; void activeSubscription?.();
    };
  });

  $effect(() => {
    if (!runtime) return;
    const counts = { rooms: Number(runtime.unreadRoomMessageCount), conversations: Number(runtime.unreadPrivateMessageCount) };
    const previous = observedUnread;
    observedUnread = counts;
    if (!previous) {
      reconcileInitialUnread();
      return;
    }
    if (counts.rooms !== previous.rooms) pendingUnreadLists.add('rooms');
    if (counts.conversations !== previous.conversations) pendingUnreadLists.add('conversations');
    reconcileInitialUnread();
    scheduleUnreadListRefresh();
  });

  $effect(() => {
    const username = initialUsername ?? '';
    if (!username || username === handledInitialUsername) return;
    handledInitialUsername = username;
    openOrCreateConversation(username);
  });

  $effect(() => {
    const username = target?.kind === 'conversation' ? activeUsername : null;
    presence = null;
    presenceError = null;
    if (!username) return;
    const request = new AbortController();
    void daemonApi.getUserPresence(username, request.signal).then((observation) => {
      if (!request.signal.aborted) presence = observation;
    }).catch((cause) => {
      if (!request.signal.aborted) presenceError = asError(cause);
    });
    return () => request.abort();
  });

  $effect(() => {
    const currentTarget = target;
    const status = liveStatus;
    if (!currentTarget || currentTarget.kind === 'draft') return;
    const request = ++activation;
    void activate(currentTarget, status === 'connected', request);
    return () => { activation += 1; };
  });

  $effect(() => {
    if (liveStatus === 'offline') {
      wasOffline = true;
      return;
    }
    if (liveStatus !== 'connected' || !wasOffline) return;
    wasOffline = false;
    if (listsError && !listsLoading) void loadLists(true, failedLists());
  });

  function failedLists(): ChatList[] {
    return chatLists.filter(kind => Boolean(listErrors[kind]));
  }

  async function loadLists(reset = false, kinds?: readonly ChatList[]): Promise<void> {
    if (disposed) return;
    if (listsLoading && !reset && (rooms.length || conversations.length)) return;
    const requested = kinds ?? chatLists.filter(kind => reset || !loadedLists.has(kind)
      || Boolean(kind === 'rooms' ? roomCursor : conversationCursor));
    if (!requested.length) return;
    // The two bounded lists share one loader, but an unrelated slow response
    // must not delay healthy data or supersede another list's request.
    await Promise.all(requested.map(async kind => {
      const request = ++listRevisions[kind];
      listLoading = { ...listLoading, [kind]: true };
      listErrors = { ...listErrors, [kind]: undefined };
      try {
        const result = kind === 'rooms'
          ? await daemonApi.getChatRooms(reset ? null : roomCursor)
          : await daemonApi.getConversations(reset ? null : conversationCursor);
        if (disposed || request !== listRevisions[kind]) return;
        if (kind === 'conversations') {
          const page = result as Schema<'ConversationPageDto'>;
          // An exact-target lookup/live snapshot can finish before this list.
          // Keep that fetched target even when it falls beyond the first page.
          const selected = activeConversation;
          conversations = reset
            ? mergeFirstPage(page.items, selected ? [selected] : [], item => item.conversationId)
            : mergeBy(conversations, page.items, item => item.conversationId);
          conversationCursor = page.nextCursor ?? null;
        } else {
          const page = result as Schema<'ChatRoomPageDto'>;
          const selected = activeRoom;
          rooms = reset
            ? mergeFirstPage(page.items, selected ? [selected] : [], item => item.roomId)
            : mergeBy(rooms, page.items, item => item.roomId);
          roomCursor = page.nextCursor ?? null;
        }
        loadedLists.add(kind);
      } catch (cause) {
        if (!disposed && request === listRevisions[kind])
          listErrors = { ...listErrors, [kind]: asError(cause) };
      } finally {
        if (!disposed && request === listRevisions[kind]) {
          listLoading = { ...listLoading, [kind]: false };
          if (!target) {
            const firstConversation = conversations[0];
            const firstRoom = rooms[0];
            if (firstConversation) selectTarget({ kind: 'conversation', id: firstConversation.conversationId });
            else if (!listLoading.conversations && firstRoom) selectTarget({ kind: 'room', id: firstRoom.roomId });
          }
          reconcileInitialUnread();
          scheduleUnreadListRefresh();
        }
      }
    }));
  }

  function reconcileInitialUnread(): void {
    if (initialUnreadReconciled || listsLoading || !observedUnread) return;
    initialUnreadReconciled = true;
    // The daemon scope and the two HTTP lists can arrive in either order.
    // Repair a list read that preceded the initial unread snapshot.
    if (rooms.reduce((count, item) => count + Number(item.unreadCount), 0) < observedUnread.rooms)
      pendingUnreadLists.add('rooms');
    if (conversations.reduce((count, item) => count + Number(item.unreadCount), 0) < observedUnread.conversations)
      pendingUnreadLists.add('conversations');
  }

  function scheduleUnreadListRefresh(): void {
    if (disposed || !pendingUnreadLists.size || listsLoading || unreadRefreshInFlight || unreadRefreshTimer) return;
    // A busy room can publish many count revisions; refresh its bounded list
    // at most once per second while retaining any later pending transition.
    const delay = Math.max(0, 1_000 - (performance.now() - lastUnreadRefreshAt));
    unreadRefreshTimer = setTimeout(() => {
      unreadRefreshTimer = null;
      void refreshUnreadLists();
    }, delay);
  }

  async function refreshUnreadLists(): Promise<void> {
    if (disposed || listsLoading || unreadRefreshInFlight) { scheduleUnreadListRefresh(); return; }
    const kinds = [...pendingUnreadLists];
    pendingUnreadLists.clear();
    unreadRefreshInFlight = true;
    lastUnreadRefreshAt = performance.now();
    const revisions = { ...listRevisions };
    try {
      const results = await Promise.allSettled(kinds.map(async (kind) => ({
        kind,
        page: kind === 'rooms' ? await daemonApi.getChatRooms(null) : await daemonApi.getConversations(null),
      })));
      if (disposed) return;
      for (let index = 0; index < results.length; index++) {
        const result = results[index]!;
        const kind = kinds[index]!;
        if (revisions[kind] !== listRevisions[kind]) {
          pendingUnreadLists.add(kind);
          continue;
        }
        if (result.status === 'rejected') {
          listErrors = { ...listErrors, [kind]: asError(result.reason) };
        } else if (result.value.kind === 'rooms') {
          const page = result.value.page as Schema<'ChatRoomPageDto'>;
          rooms = mergeFirstPage(page.items, rooms, (item) => item.roomId);
          if (!loadedLists.has(kind)) roomCursor = page.nextCursor ?? null;
        } else {
          const page = result.value.page as Schema<'ConversationPageDto'>;
          conversations = mergeFirstPage(page.items, conversations, (item) => item.conversationId);
          if (!loadedLists.has(kind)) conversationCursor = page.nextCursor ?? null;
        }
        if (result.status === 'fulfilled') {
          listErrors = { ...listErrors, [kind]: undefined };
          loadedLists.add(kind);
        }
      }
    } finally {
      unreadRefreshInFlight = false;
      scheduleUnreadListRefresh();
    }
  }

  async function activate(next: Exclude<Target, { kind: 'draft' }>, useLive: boolean, request: number): Promise<void> {
    await activeSubscription?.();
    if (request !== activation || target?.id !== next.id) return;
    activeSubscription = null;
    // Selection changes clear the thread in selectTarget. Connection changes
    // must not erase already loaded history or the reader's position.
    const refreshing = targetSnapshot !== null;
    messagesLoading = true;
    targetError = null;
    try {
      const [snapshot, page] = await Promise.all([
        next.kind === 'conversation' ? daemonApi.getConversationSnapshot(next.id) : daemonApi.getRoomSnapshot(next.id),
        next.kind === 'conversation' ? daemonApi.getConversationMessages(next.id) : daemonApi.getRoomMessages(next.id),
      ]);
      if (request !== activation || target?.id !== next.id) return;
      const previousMessages = mergeMessages(historicalMessages, targetSnapshot?.messages ?? []);
      // The live snapshot may already have recovered independently. Only the
      // paged history establishes a contiguous boundary for catch-up.
      const newestKnown = historyBoundary;
      const refreshedPages = [page.items];
      let oldestRefreshed = page.items[0];
      let refreshedCursor = page.nextCursor;
      // Catch up through the existing paged history API when an outage spans
      // more than one page; otherwise retaining older rows would hide a gap.
      while (refreshing && newestKnown && refreshedCursor
          && oldestRefreshed
          && wireInteger(oldestRefreshed.sequence) > wireInteger(newestKnown.sequence)) {
        const earlier = next.kind === 'conversation'
          ? await daemonApi.getConversationMessages(next.id, refreshedCursor)
          : await daemonApi.getRoomMessages(next.id, refreshedCursor);
        if (request !== activation || target?.id !== next.id) return;
        refreshedPages.push(earlier.items);
        oldestRefreshed = earlier.items[0];
        refreshedCursor = earlier.nextCursor;
      }
      // History pages are chronological internally and fetched newest first.
      // Assemble once rather than copying and sorting the growing catch-up
      // history on every page.
      const refreshedMessages = refreshedPages.reverse().flat();
      const snapshotTarget = snapshot.chatTarget;
      if (!snapshotTarget) throw new Error('The daemon returned a chat snapshot without its target.');
      const followLatest = !refreshing || !messagesElement
        || messagesElement.scrollHeight - messagesElement.scrollTop - messagesElement.clientHeight <= 2;
      historicalMessages = refreshing
        ? mergeMessages(previousMessages, refreshedMessages)
        : refreshedMessages;
      historyBoundary = refreshedMessages.at(-1) ?? newestKnown;
      targetSnapshot = snapshotTarget;
      if (!refreshing || !newestKnown) messageCursor = page.nextCursor ?? null;
      updateTargetSummary(snapshotTarget);
      markRead(snapshotTarget);
      if (followLatest) scheduleScrollToBottom();
      if (useLive) {
        const unsubscribe = await liveClient.subscribeChat(next.kind, next.id, (value, observedMessages, snapshotRefresh) => {
          if (request !== activation || target?.id !== next.id) return;
          const followLatest = !messagesElement || messagesElement.scrollHeight - messagesElement.scrollTop - messagesElement.clientHeight <= 2;
          // Retain displayed messages before replacing the bounded live tail.
          historicalMessages = mergeMessages(historicalMessages, observedMessages);
          targetSnapshot = value;
          updateTargetSummary(value);
          markRead(value);
          if (followLatest) scheduleScrollToBottom();
          if (snapshotRefresh && snapshotNeedsHistory(historyBoundary, value.messages))
            void reconcileSnapshotHistory(next, request, ++reconciliationRevision);
        });
        if (request !== activation || target?.id !== next.id) await unsubscribe();
        else activeSubscription = unsubscribe;
      }
    } catch (cause) {
      if (request === activation) targetError = asError(cause);
    } finally {
      if (request === activation) messagesLoading = false;
    }
  }

  async function reconcileSnapshotHistory(
    selected: Exclude<Target, { kind: 'draft' }>,
    request: number,
    reconciliation: number,
  ): Promise<void> {
    const boundary = historyBoundary;
    const pages: ChatMessageDto[][] = [];
    let cursor: string | null = null;
    try {
      do {
        const page: Schema<'ChatMessagePageDto'> = selected.kind === 'conversation'
          ? await daemonApi.getConversationMessages(selected.id, cursor)
          : await daemonApi.getRoomMessages(selected.id, cursor);
        if (request !== activation || reconciliation !== reconciliationRevision
            || target?.id !== selected.id) return;
        pages.push(page.items);
        const oldest = page.items[0];
        cursor = page.nextCursor ?? null;
        if (boundary && oldest && wireInteger(oldest.sequence) <= wireInteger(boundary.sequence))
          break;
      } while (cursor);
      const oldestFetched = pages.at(-1)?.[0];
      if (boundary && (!oldestFetched
          || wireInteger(oldestFetched.sequence) > wireInteger(boundary.sequence)))
        throw new Error('The missing chat history is no longer available.');
      const newest = pages[0]?.at(-1);
      historicalMessages = mergeMessages(historicalMessages, pages.reverse().flat());
      if (!boundary) messageCursor = cursor;
      if (newest) historyBoundary = newest;
      targetError = null;
    } catch (cause) {
      if (request === activation && reconciliation === reconciliationRevision)
        targetError = asError(cause);
    }
  }

  function selectTarget(next: Target): void {
    selectionRevision++;
    void activeSubscription?.();
    activeSubscription = null;
    target = next;
    targetSnapshot = null;
    historicalMessages = [];
    historyBoundary = undefined;
    messageCursor = null;
    messagesLoading = false;
    targetError = null;
    menuOpen = false;
    mutation = { phase: 'idle' };
    draft = '';
  }

  function openOrCreateConversation(username: string): void {
    if (!username.trim()) return;
    const existing = conversations.find((item) => item.username === username);
    selectTarget(existing ? { kind: 'conversation', id: existing.conversationId } : { kind: 'draft', id: `draft:${username}`, username });
    if (!existing) void resolveDraftConversation(username, selectionRevision);
  }

  async function resolveDraftConversation(username: string, revision: number): Promise<void> {
    try {
      const existing = await daemonApi.getConversationByPeer(username);
      if (revision !== selectionRevision || target?.kind !== 'draft' || target.username !== username) return;
      targetError = null;
      if (!existing || mutation.phase === 'pending') return;
      conversations = mergeBy(conversations, [existing], item => item.conversationId);
      const composed = draft;
      selectTarget({ kind: 'conversation', id: existing.conversationId });
      draft = composed;
    } catch (cause) {
      if (revision === selectionRevision) targetError = asError(cause);
    }
  }

  async function loadEarlier(): Promise<void> {
    if (!target || target.kind === 'draft' || !messageCursor || messagesLoading) return;
    const selected = target;
    const revision = selectionRevision;
    const request = activation;
    messagesLoading = true;
    try {
      const page = selected.kind === 'conversation'
        ? await daemonApi.getConversationMessages(selected.id, messageCursor)
        : await daemonApi.getRoomMessages(selected.id, messageCursor);
      if (revision !== selectionRevision || request !== activation) return;
      const container = messagesElement;
      const top = container?.getBoundingClientRect().top ?? 0;
      const anchor = container ? Array.from(container.querySelectorAll<HTMLElement>('[data-message-id]'))
        .find(element => element.getBoundingClientRect().bottom > top) : undefined;
      const anchorTop = anchor?.getBoundingClientRect().top;
      historicalMessages = mergeMessages(page.items, historicalMessages);
      messageCursor = page.nextCursor ?? null;
      await tick();
      if (revision === selectionRevision && request === activation && container === messagesElement
          && container && anchor?.isConnected && anchorTop !== undefined) {
        container.scrollTop += anchor.getBoundingClientRect().top - anchorTop;
      }
    } catch (cause) { if (revision === selectionRevision && request === activation) targetError = asError(cause); }
    finally { if (revision === selectionRevision && request === activation) messagesLoading = false; }
  }

  async function retryChat(): Promise<void> {
    if (listsError) await loadLists(true, failedLists());
    const selected = target;
    if (targetError && selected?.kind === 'draft') {
      await resolveDraftConversation(selected.username, selectionRevision);
    }
    if (targetError && selected && selected.kind !== 'draft') {
      await activate(selected, liveStatus === 'connected', ++activation);
    }
  }

  async function sendMessage(): Promise<void> {
    const text = draft;
    if (!text.trim() || !target || composerDisabled || mutation.phase === 'pending') return;
    if (!acceptComposerText(text)) return;
    const selected = target;
    const revision = selectionRevision;
    const messageId = randomUUID();
    mutation = { phase: 'pending', label: 'Sending…' };
    draft = '';
    try {
      const message = selected.kind === 'draft'
        ? await daemonApi.sendPrivateMessage(selected.username, text, messageId)
        : selected.kind === 'conversation'
          ? await daemonApi.sendConversationMessage(selected.id, text, messageId)
          : await daemonApi.sendRoomMessage(selected.id, text, messageId);
      if (revision !== selectionRevision) return;
      historicalMessages = mergeMessages(historicalMessages, [message]);
      mutation = { phase: message.state === 'Failed' ? 'rejected' : 'succeeded', label: message.state === 'Failed' ? 'Message failed' : 'Message sent', detail: message.failureReason ?? undefined };
      if (selected.kind === 'draft') {
        const username = selected.username;
        await loadLists(true, ['conversations']);
        if (revision !== selectionRevision) return;
        const created = conversations.find((item) => item.conversationId === message.targetId || item.username === username);
        if (created) selectTarget({ kind: 'conversation', id: created.conversationId });
      }
      scheduleScrollToBottom();
    } catch (cause) {
      if (revision !== selectionRevision) return;
      draft = text;
      mutation = { phase: 'rejected', label: 'Message failed', detail: asError(cause).message };
    }
  }

  async function joinRoom(): Promise<void> {
    const name = addDraft.trim();
    if (!name) return;
    mutation = { phase: 'pending', label: 'Joining room…' };
    try {
      const room = await daemonApi.joinRoom(name);
      rooms = mergeBy([], [room, ...rooms], (item) => item.roomId);
      adding = null;
      addDraft = '';
      selectTarget({ kind: 'room', id: room.roomId });
      mutation = { phase: 'succeeded', label: 'Room joined' };
    } catch (cause) { mutation = { phase: 'rejected', label: 'Join failed', detail: asError(cause).message }; }
  }

  function submitAdd(): void {
    if (adding === 'room') void joinRoom();
    else if (adding === 'user') { openOrCreateConversation(addDraft); addDraft = ''; adding = null; }
  }

  async function blockActiveUser(): Promise<void> {
    if (!activeUsername || mutation.phase === 'pending') return;
    const username = activeUsername;
    const revision = selectionRevision;
    mutation = { phase: 'pending', label: 'Updating peer access…' };
    try {
      const restrictions = await daemonApi.setPrivateMessageBlocked(username, !activeUserBlocked);
      const blocked = restrictions.privateMessages.isBlocked;
      conversations = conversations.map((item) => item.username === username ? { ...item, privateMessagesBlocked: blocked } : item);
      if (revision !== selectionRevision) return;
      if (targetSnapshot?.conversation) targetSnapshot = { ...targetSnapshot, conversation: { ...targetSnapshot.conversation, privateMessagesBlocked: blocked } };
      mutation = { phase: 'succeeded', label: blocked ? 'User blocked' : 'User unblocked' };
      menuOpen = false;
    } catch (cause) { if (revision === selectionRevision) mutation = { phase: 'rejected', label: 'Restriction update failed', detail: asError(cause).message }; }
  }

  async function deleteActiveConversation(): Promise<void> {
    if (!activeConversation || mutation.phase === 'pending') return;
    const id = activeConversation.conversationId;
    const revision = selectionRevision;
    mutation = { phase: 'pending', label: 'Deleting chat…' };
    try {
      await daemonApi.deleteConversationHistory(id);
      await daemonApi.archiveConversation(id);
      conversations = conversations.filter((item) => item.conversationId !== id);
      if (revision !== selectionRevision) return;
      target = null;
      mutation = { phase: 'succeeded', label: 'Chat deleted' };
      menuOpen = false;
      await loadLists(true, ['conversations']);
    } catch (cause) { if (revision === selectionRevision) mutation = { phase: 'rejected', label: 'Delete failed', detail: asError(cause).message }; }
  }

  async function leaveActiveRoom(): Promise<void> {
    if (!activeRoom || mutation.phase === 'pending') return;
    const id = activeRoom.roomId;
    const revision = selectionRevision;
    mutation = { phase: 'pending', label: 'Leaving room…' };
    try {
      await daemonApi.leaveRoom(id);
      rooms = rooms.filter((item) => item.roomId !== id);
      if (revision !== selectionRevision) return;
      target = null;
      mutation = { phase: 'succeeded', label: 'Room left' };
      menuOpen = false;
      await loadLists(true, ['rooms']);
    } catch (cause) { if (revision === selectionRevision) mutation = { phase: 'rejected', label: 'Leave failed', detail: asError(cause).message }; }
  }

  function updateTargetSummary(value: ChatTargetSnapshotDto | null): void {
    if (value?.conversation) conversations = mergeBy(conversations, [value.conversation], (item) => item.conversationId);
    if (value?.room) rooms = mergeBy(rooms, [value.room], (item) => item.roomId);
  }

  function markRead(value: ChatTargetSnapshotDto | null): void {
    if (!value) return;
    const last = value.messages.at(-1);
    if (!last) return;
    if (markedRead.get(value.targetId) === last.messageId) return;
    if (value.conversation && Number(value.conversation.unreadCount) > 0) {
      markedRead.set(value.targetId, last.messageId);
      void daemonApi.markConversationRead(value.targetId, last.messageId).catch(() => markedRead.delete(value.targetId));
    }
    if (value.room && Number(value.room.unreadCount) > 0) {
      markedRead.set(value.targetId, last.messageId);
      void daemonApi.markRoomRead(value.targetId, last.messageId).catch(() => markedRead.delete(value.targetId));
    }
  }

  function preview(message: ChatMessageDto | null | undefined): string { return message?.text.trim() || 'No messages yet'; }
  function messageTime(message: ChatMessageDto): string { return new Date(message.occurredAtUtc).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
  function mergeBy<T>(left: T[], right: T[], id: (value: T) => string): T[] { const values = new Map(left.map((item) => [id(item), item])); for (const item of right) values.set(id(item), item); return [...values.values()]; }
  function mergeFirstPage<T extends { revision: number | string }>(fresh: T[], loaded: T[], id: (value: T) => string): T[] {
    const existing = new Map(loaded.map((item) => [id(item), item]));
    const seen = new Set(fresh.map(id));
    return [
      ...fresh.map((item) => {
        const previous = existing.get(id(item));
        return previous && wireInteger(previous.revision) > wireInteger(item.revision) ? previous : item;
      }),
      ...loaded.filter((item) => !seen.has(id(item))),
    ];
  }
  function scheduleScrollToBottom(): void {
    const revision = selectionRevision;
    requestAnimationFrame(() => {
      if (revision === selectionRevision && messagesElement) messagesElement.scrollTop = messagesElement.scrollHeight;
    });
  }
  $effect(() => {
    draft;
    resizeComposer();
  });
  function resizeComposer(): void {
    const node = composerTextarea;
    if (!node) return;
    node.style.height = '38px';
    node.style.overflowY = 'hidden';
    if (!node.value) return;
    node.style.height = `${Math.min(156, Math.max(38, node.scrollHeight + 2))}px`;
    node.style.overflowY = node.scrollHeight > node.clientHeight ? 'auto' : 'hidden';
  }
  function pasteComposerText(event: ClipboardEvent & { currentTarget: HTMLTextAreaElement }): void {
    const pasted = event.clipboardData?.getData('text/plain');
    if (pasted === undefined) return;
    const singleLine = pasted.replace(/\r\n|[\r\n\v\f\u0085\u2028\u2029]/g, ' ');
    if (singleLine === pasted) return;
    event.preventDefault();
    const input = event.currentTarget;
    input.setRangeText(singleLine, input.selectionStart ?? input.value.length, input.selectionEnd ?? input.value.length, 'end');
    draft = input.value;
  }
  function acceptComposerText(text: string): boolean {
    if (!/[\r\n]/.test(text)) return true;
    mutation = { phase: 'rejected', label: 'Soulseek messages cannot contain line breaks.' };
    return false;
  }
  function asError(cause: unknown): Error { return cause instanceof Error ? cause : new Error(String(cause)); }
</script>

<svelte:window
  onresize={resizeComposer}
  onkeydown={(event) => {
    if (event.key === 'Escape') {
      menuOpen = false;
      adding = null;
    }
  }}
  onpointerdown={(event) => {
    if (menuOpen && chatOptionsElement && event.target instanceof Node
      && !chatOptionsElement.contains(event.target)) menuOpen = false;
  }}
/>

<section class="page chat-page">
  <div class="chat-layout">
    <aside class="chat-sidebar" aria-label="Chat destinations">
      <section class="chat-sidebar-section">
        <header class="chat-section-heading">
          <span>Rooms</span>
          <button type="button" class="chat-add-button" aria-label="Join room" title="Join room"
            onclick={() => { adding = adding === 'room' ? null : 'room'; addDraft = ''; }}>+</button>
        </header>
        {#if adding === 'room'}
          <div class="chat-add-row">
            <input aria-label="Room name" placeholder="Room name…" bind:value={addDraft}
              onkeydown={(event) => {
                if (event.key === 'Enter') submitAdd();
                else if (event.key === 'Escape') adding = null;
              }}/>
            <button type="button" disabled={!addDraft.trim()} onclick={submitAdd}>Join</button>
          </div>
        {/if}
        <div class="chat-destination-list">
          {#each rooms as room (room.roomId)}
            <button type="button" class:active={target?.kind === 'room' && target.id === room.roomId}
              class="chat-destination room"
              aria-current={target?.kind === 'room' && target.id === room.roomId ? 'page' : undefined}
              onclick={() => selectTarget({ kind: 'room', id: room.roomId })}>
              <span class="room-mark">#</span>
              <span class="chat-destination-copy">
                <strong>{room.name}</strong>
                <small>{room.phase !== 'Joined' ? room.phase : preview(room.lastMessage)}</small>
              </span>
              {#if Number(room.unreadCount) > 0}<span class="unread-count">{room.unreadCount}</span>{/if}
            </button>
          {/each}
        </div>
      </section>
      <section class="chat-sidebar-section users-section">
        <header class="chat-section-heading">
          <span>Users</span>
          <button type="button" class="chat-add-button" aria-label="Message user" title="Message user"
            onclick={() => { adding = adding === 'user' ? null : 'user'; addDraft = ''; }}>+</button>
        </header>
        {#if adding === 'user'}
          <div class="chat-add-row">
            <input aria-label="Username" placeholder="Username…" bind:value={addDraft}
              onkeydown={(event) => {
                if (event.key === 'Enter') submitAdd();
                else if (event.key === 'Escape') adding = null;
              }}/>
            <button type="button" disabled={!addDraft.trim()} onclick={submitAdd}>Open</button>
          </div>
        {/if}
        <div class="chat-destination-list">
          {#if target?.kind === 'draft'}
            <div class="chat-destination user draft-target active">
              <button type="button" class="chat-destination-open"
                aria-label={`Open draft conversation with ${target.username}`}></button>
              <span class="avatar">{userInitials(target.username)}</span>
              <span class="chat-destination-copy">
                <strong>{target.username}</strong>
                <small>{targetError ? 'History unavailable' : 'New chat'}</small>
              </span>
            </div>
          {/if}
          {#each conversations as conversation (conversation.conversationId)}
            <div class:active={target?.kind === 'conversation' && target.id === conversation.conversationId}
              class="chat-destination user">
              <button type="button" class="chat-destination-open"
                aria-label={`Open conversation with ${conversation.username}`}
                aria-current={target?.kind === 'conversation' && target.id === conversation.conversationId ? 'page' : undefined}
                onclick={() => selectTarget({ kind: 'conversation', id: conversation.conversationId })}></button>
              <span class="avatar">{userInitials(conversation.username)}</span>
              <span class="chat-destination-copy">
                <strong>{conversation.username}</strong>
                <small>{preview(conversation.lastMessage)}</small>
              </span>
              {#if Number(conversation.unreadCount) > 0}
                <span class="unread-count">{conversation.unreadCount}</span>
              {/if}
            </div>
          {/each}
        </div>
        {#if conversationCursor || roomCursor}
          <LoadMoreButton loading={listsLoading} onclick={() => void loadLists()}/>
        {/if}
      </section>
    </aside>

    <div class="chat-thread">
      <header class="chat-thread-heading">
        {#if activeConversation || target?.kind === 'draft'}
          <div class="chat-thread-identity">
            <UsernameLink username={activeUsername ?? ''} actions={userActions}/>
            <small class:blocked={activeUserBlocked}
              title={presenceError?.message ?? (presence?.status.state !== 'available' ? presence?.status.reason ?? undefined : undefined)}>{activeUserBlocked ? 'blocked' : target?.kind === 'draft' ? 'new chat' : presenceLabel}</small>
          </div>
        {:else if activeRoom}
          <div class="chat-thread-identity">
            <strong>#{activeRoom.name}</strong>
            <small>{activeRoom.kind === 'Private' ? 'Private room' : 'Public room'} · {formatCount(activeRoom.memberCount)} users · {activeRoom.rosterComplete ? 'roster complete' : 'roster partial'}{activeRoom.phase !== 'Joined' ? ` · ${activeRoom.phase}` : ''}</small>
          </div>
        {/if}
        <div class="chat-thread-options" bind:this={chatOptionsElement}>
          <button type="button" class="chat-options-button" aria-label="Chat options"
            aria-haspopup="menu" aria-expanded={menuOpen} disabled={!target}
            onclick={() => (menuOpen = !menuOpen)}><Icon name="more"/></button>
          {#if menuOpen}
            <div class="chat-options-menu" role="menu" aria-label="Chat options"
              use:anchoredMenu={{ anchor: chatOptionsElement, align: 'end' }}>
              {#if activeConversation || target?.kind === 'draft'}
                <button type="button" role="menuitem"
                  onclick={() => void blockActiveUser()}>{activeUserBlocked ? 'Unblock user' : 'Block user'}</button>
                {#if activeConversation}
                  <button type="button" role="menuitem" class="danger"
                    onclick={() => void deleteActiveConversation()}>Delete chat</button>
                {/if}
              {:else if activeRoom}
                <button type="button" role="menuitem" class="danger"
                  onclick={() => void leaveActiveRoom()}>Leave room</button>
              {/if}
            </div>
          {/if}
        </div>
      </header>

      <div class="chat-resource-state">
        {#if listsError || targetError}
          <div class={`resource-state-notice ${liveStatus === 'offline' ? 'offline' : 'unavailable'}`} role="alert">
            <span class="resource-state-dot"></span>
            <span class="resource-state-copy">
              <strong>{liveStatus === 'offline' ? 'Daemon unavailable' : 'Chat unavailable'}</strong>
              <small>{liveStatus === 'offline' ? 'This resource cannot be loaded while the daemon is offline.' : (targetError ?? listsError)?.message}</small>
            </span>
            {#if liveStatus !== 'offline'}
              <button type="button" disabled={listsLoading || messagesLoading}
                onclick={() => void retryChat()}>Retry</button>
            {/if}
          </div>
        {/if}
        <MutationStatus state={mutation}/>
      </div>
      <div class:short-thread={activeMessages.length < 6} class="chat-messages" aria-live="polite" bind:this={messagesElement}>
        {#if messageCursor}
          <LoadMoreButton loading={messagesLoading}
            onclick={() => void loadEarlier()}/>
        {/if}
        {#if messagesLoading && !activeMessages.length && target?.kind !== 'draft'}
          <LoadingIndicator label="Loading messages" />
        {:else if listsLoading && !target && !listsError}
          <LoadingIndicator label="Loading chat" />
        {:else if activeMessages.length === 0}
          <div class="chat-empty-thread">
            <strong>{targetError || (listsError && !target) ? 'Messages unavailable' : target?.kind === 'draft' ? 'New chat' : target ? 'No messages yet' : 'Choose a room or user'}</strong>
          </div>
        {:else if activeRoom}
          {#each activeMessages as message (message.messageId)}
            <div class:mine={message.direction === 'Outgoing'}
              class:send-failed={message.state === 'Failed'}
              class="room-message" data-message-id={message.messageId}>
              <div class="room-message-meta">
                {#if message.direction === 'Outgoing'}
                  <span class="room-message-self">{message.sender}</span>
                {:else}
                  <UsernameLink username={message.sender} actions={userActions}/>
                {/if}
                <time>{messageTime(message)}</time>
                {#if message.direction === 'Outgoing'}
                  <span class={`message-send-state ${message.state.toLowerCase()}`} title={message.state === 'Sent' ? 'Sent to the Soulseek server; recipient delivery is not confirmed.' : undefined}>{message.state}{message.failureReason ? ` · ${message.failureReason}` : ''}</span>
                {/if}
              </div>
              <div class="message-body"><LinkifiedText text={message.text}/></div>
            </div>
          {/each}
        {:else}
          {#each activeMessages as message (message.messageId)}
            <div class:mine={message.direction === 'Outgoing'}
              class:send-failed={message.state === 'Failed'}
              class="direct-message-row" data-message-id={message.messageId}>
              <div class="direct-message-bubble">
                <div class="message-body"><LinkifiedText text={message.text}/></div>
                <div class="direct-message-meta">
                  <time>{messageTime(message)}</time>
                  {#if message.direction === 'Outgoing'}
                    <span class={`message-send-state ${message.state.toLowerCase()}`} title={message.state === 'Sent' ? 'Sent to the Soulseek server; recipient delivery is not confirmed.' : undefined}>{message.state}{message.failureReason ? ` · ${message.failureReason}` : ''}</span>
                  {/if}
                </div>
              </div>
            </div>
          {/each}
        {/if}
      </div>
      <div class="chat-composer">
        {#if activeUserBlocked}
          <span class="composer-state">Unblock {activeUsername} to send messages.</span>
        {:else if liveStatus === 'offline'}
          <span class="composer-state">Chat disabled · Daemon unavailable</span>
        {:else if liveStatus !== 'connected'}
          <span class="composer-state">Chat {liveStatus === 'reconnecting' ? 'reconnecting' : liveStatus === 'connecting' ? 'connecting' : 'unavailable'}</span>
        {:else if runtime?.state !== 'Ready'}
          <span class="composer-state">Chat {(runtime?.state ?? 'Unavailable').toLowerCase()}{runtime?.reason ? ` · ${runtime.reason}` : ''}</span>
        {/if}
        <div class="chat-composer-row">
          <textarea rows="1" wrap="soft" aria-label="Message"
            placeholder={activeRoom ? `Message #${activeRoom.name}…` : activeUsername ? `Message ${activeUsername}…` : 'Message…'}
            bind:value={draft} bind:this={composerTextarea} disabled={composerDisabled}
            onpaste={pasteComposerText}
            ondrop={(event) => { if (!acceptComposerText(event.dataTransfer?.getData('text/plain') ?? '')) event.preventDefault(); }}
            onkeydown={(event) => {
              if (event.key === 'Enter' && !event.isComposing) {
                event.preventDefault();
                if (event.ctrlKey || event.metaKey) void sendMessage();
              }
            }}></textarea>
          <button type="button" title="Send (Ctrl+Enter)" disabled={composerDisabled || !draft.trim()}
            onclick={() => void sendMessage()}>Send</button>
        </div>
      </div>
    </div>
  </div>
</section>
