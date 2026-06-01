import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  Activity,
  AlertTriangle,
  ArrowLeft,
  CheckCircle2,
  Clock3,
  FileText,
  GitBranch,
  Home,
  MessageSquareText,
  RefreshCw,
  ShieldCheck,
  SquareTerminal,
  UsersRound
} from 'lucide-react';
import { Badge } from './components/ui/badge';
import { Button } from './components/ui/button';
import { Card } from './components/ui/card';
import { Tabs } from './components/ui/tabs';
import './styles.css';

const POLL_MS = 5000;

function App() {
  const [route, setRoute] = useState(readRoute);
  const [autoRefresh, setAutoRefresh] = useState(true);
  const dataState = useObserverData(route, autoRefresh);

  useEffect(() => {
    const onPopState = () => setRoute(readRoute());
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  const navigate = useCallback((to) => {
    window.history.pushState({}, '', to);
    setRoute(readRoute());
  }, []);

  return (
    <div className="app-shell">
      <TopBar
        route={route}
        autoRefresh={autoRefresh}
        onToggleAutoRefresh={() => setAutoRefresh((value) => !value)}
        onRefresh={dataState.refresh}
        isRefreshing={dataState.loading}
        refreshedAt={dataState.refreshedAt}
        navigate={navigate}
      />

      {dataState.error ? (
        <ErrorState error={dataState.error} onRefresh={dataState.refresh} />
      ) : dataState.loading && !dataState.data ? (
        <LoadingState />
      ) : (
        <main>
          {route.kind === 'mission' ? (
            <MissionDetail bundle={dataState.data} navigate={navigate} />
          ) : route.kind === 'session' ? (
            <SessionDetail detail={dataState.data} navigate={navigate} />
          ) : (
            <Overview snapshot={dataState.data} navigate={navigate} />
          )}
        </main>
      )}
    </div>
  );
}

function useObserverData(route, autoRefresh) {
  const [state, setState] = useState({
    data: null,
    error: null,
    loading: true,
    refreshedAt: null
  });
  const key = `${route.kind}:${route.id ?? ''}`;

  const refresh = useCallback(async () => {
    setState((current) => ({ ...current, loading: true, error: null }));
    try {
      const data = await fetchJson(routeToApi(route));
      setState({ data, error: null, loading: false, refreshedAt: new Date() });
    } catch (error) {
      setState((current) => ({
        ...current,
        error: error instanceof Error ? error : new Error(String(error)),
        loading: false
      }));
    }
  }, [key]);

  useEffect(() => {
    refresh();
  }, [refresh]);

  useEffect(() => {
    if (!autoRefresh) return undefined;
    const interval = window.setInterval(refresh, POLL_MS);
    return () => window.clearInterval(interval);
  }, [autoRefresh, refresh]);

  return { ...state, refresh };
}

function TopBar({ route, autoRefresh, onToggleAutoRefresh, onRefresh, isRefreshing, refreshedAt, navigate }) {
  return (
    <header className="top-bar">
      <div className="brand-block">
        <button className="icon-link" type="button" title="Overview" aria-label="Overview" onClick={() => navigate('/')}>
          <Home size={18} />
        </button>
        <div>
          <p className="eyebrow">Harness CLI</p>
          <h1>{route.kind === 'overview' ? 'Work Map Observer' : route.kind === 'mission' ? 'Mission Detail' : 'Session Detail'}</h1>
        </div>
      </div>
      <div className="top-actions">
        <span className="refresh-time">{refreshedAt ? `Updated ${formatTime(refreshedAt)}` : 'Not loaded'}</span>
        <Button variant="ghost" selected={autoRefresh} type="button" onClick={onToggleAutoRefresh}>
          <Activity size={16} />
          <span>{autoRefresh ? 'Polling' : 'Paused'}</span>
        </Button>
        <Button type="button" onClick={onRefresh} disabled={isRefreshing}>
          <RefreshCw size={16} className={isRefreshing ? 'spin' : ''} />
          <span>Refresh</span>
        </Button>
      </div>
    </header>
  );
}

function Overview({ snapshot, navigate }) {
  const bundles = snapshot?.missions ?? [];
  const sessions = useMemo(
    () =>
      bundles
        .flatMap((bundle) =>
          (bundle.sessions ?? []).map((session) => ({
            session,
            mission: bundle.mission,
            workstream: (bundle.workstreams ?? []).find((item) => item.id === session.workstreamId)
          }))
        )
        .sort((left, right) => dateValue(right.session.updatedAtUtc) - dateValue(left.session.updatedAtUtc)),
    [bundles]
  );
  const visibleSessions = sessions.filter(({ session }) => !isArchived(session.status));
  const archivedSessions = sessions.length - visibleSessions.length;
  const activeSessions = visibleSessions.filter(({ session }) => !['handoff', 'blocked', 'done', 'complete'].includes(normalize(session.status))).length;

  return (
    <div className="page-stack">
      <section className="summary-grid">
        <StatCard label="Missions" value={bundles.length} icon={GitBranch} />
        <StatCard label="Sessions" value={visibleSessions.length} icon={UsersRound} />
        <StatCard label="Active" value={activeSessions} icon={Activity} />
        <StatCard label="Store" value={shortPath(snapshot?.dataDirectory)} icon={SquareTerminal} compact />
      </section>

      <Section
        title="Missions"
        aside={bundles.length > 0 ? `${bundles.length} records` : undefined}
      >
        {bundles.length === 0 ? (
          <EmptyState title="No missions recorded" body="Create a work-map mission and this observer will show it here." />
        ) : (
          <div className="card-grid">
            {bundles.map((bundle) => (
              <MissionCard key={bundle.mission.id} bundle={bundle} navigate={navigate} />
            ))}
          </div>
        )}
      </Section>

      <Section title="Recent Sessions" aside={visibleSessions.length > 0 ? `${visibleSessions.length} linked${archivedSessions > 0 ? `, ${archivedSessions} archived` : ''}` : undefined}>
        {visibleSessions.length === 0 ? (
          <EmptyState title="No sessions linked" body="Linked or run sessions will appear after work-map records are written." />
        ) : (
          <div className="card-grid">
            {visibleSessions.slice(0, 12).map(({ session, mission, workstream }) => (
              <SessionCard
                key={session.id}
                session={session}
                mission={mission}
                workstream={workstream}
                navigate={navigate}
              />
            ))}
          </div>
        )}
      </Section>
    </div>
  );
}

function MissionDetail({ bundle, navigate }) {
  const [tab, setTab] = useState('sessions');
  const mission = bundle?.mission;
  const workstreams = bundle?.workstreams ?? [];
  const sessions = bundle?.sessions ?? [];
  const visibleSessions = sessions.filter((session) => !isArchived(session.status));
  const archivedSessions = sessions.filter((session) => isArchived(session.status));
  const sessionTimeline = sessions.flatMap((session) =>
    (session.events ?? []).map((event) => ({
      atUtc: event.atUtc,
      type: `${displaySessionName(session)}: ${event.type}`,
      summary: event.summary
    }))
  );
  const timeline = [...(mission?.events ?? []), ...sessionTimeline].sort((left, right) => dateValue(right.atUtc) - dateValue(left.atUtc));

  if (!mission) {
    return <EmptyState title="Mission not found" body="The mission record could not be read." />;
  }

  return (
    <div className="page-stack">
      <BackButton onClick={() => navigate('/')} label="Overview" />
      <Card className="hero-card">
        <div className="hero-row">
          <div>
            <div className="badge-row">
              <Badge tone={statusTone(mission.status)}>{mission.status}</Badge>
              <Badge>{mission.id}</Badge>
            </div>
            <h2>{mission.title}</h2>
            {mission.intent ? <p className="lead">{mission.intent}</p> : null}
          </div>
          <div className="date-stack">
            <span>Created {formatDate(mission.createdAtUtc)}</span>
            <span>Updated {formatDate(mission.updatedAtUtc)}</span>
          </div>
        </div>
        {mission.nextAction ? (
          <div className="callout">
            <Clock3 size={18} />
            <span>{mission.nextAction}</span>
          </div>
        ) : null}
      </Card>

      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          ['sessions', 'Sessions'],
          ['workstreams', 'Workstreams'],
          ['evidence', 'Evidence'],
          ['timeline', 'Timeline']
        ]}
      />

      {tab === 'sessions' ? (
        <Section title="Sessions" aside={`${visibleSessions.length} linked${archivedSessions.length > 0 ? `, ${archivedSessions.length} archived` : ''}`}>
          <div className="card-grid">
            {visibleSessions.map((session) => (
              <SessionCard
                key={session.id}
                session={session}
                mission={mission}
                workstream={workstreams.find((item) => item.id === session.workstreamId)}
                navigate={navigate}
                detailed
              />
            ))}
          </div>
          {visibleSessions.length === 0 ? <EmptyState title="No sessions" body="No active session records are linked to this mission." /> : null}
          {archivedSessions.length > 0 ? (
            <Section title="Archived Sessions" aside={`${archivedSessions.length} archived`}>
              <div className="card-grid">
                {archivedSessions.map((session) => (
                  <SessionCard
                    key={session.id}
                    session={session}
                    mission={mission}
                    workstream={workstreams.find((item) => item.id === session.workstreamId)}
                    navigate={navigate}
                    detailed
                  />
                ))}
              </div>
            </Section>
          ) : null}
        </Section>
      ) : null}

      {tab === 'workstreams' ? (
        <Section title="Workstreams" aside={`${workstreams.length} records`}>
          <div className="card-grid">
            {workstreams.map((stream) => (
              <WorkstreamCard key={stream.id} stream={stream} />
            ))}
          </div>
          {workstreams.length === 0 ? <EmptyState title="No workstreams" body="No workstream records are linked to this mission." /> : null}
        </Section>
      ) : null}

      {tab === 'evidence' ? (
        <EvidencePanel evidence={mission.evidence ?? []} title="Mission Evidence" />
      ) : null}

      {tab === 'timeline' ? (
        <Timeline items={timeline} title="Status Timeline" />
      ) : null}
    </div>
  );
}

function SessionDetail({ detail, navigate }) {
  const [tab, setTab] = useState('context');
  const session = detail?.session;
  const mission = detail?.mission;
  const workstream = detail?.workstream;

  if (!session) {
    return <EmptyState title="Session not found" body="The session record could not be read." />;
  }

  const observations = session.statusObservations ?? [];
  const events = session.events ?? [];
  const timeline = [
    ...events.map((event) => ({ atUtc: event.atUtc, type: event.type, summary: event.summary })),
    ...observations.map((item) => ({
      atUtc: item.atUtc,
      type: 'status',
      summary: `${item.effectiveStatus || item.derivedStatus || 'observed'}; messages ${item.messageCount ?? 0}`
    }))
  ].sort((left, right) => dateValue(right.atUtc) - dateValue(left.atUtc));

  return (
    <div className="page-stack">
      <BackButton onClick={() => navigate(mission ? `/missions/${encodeURIComponent(mission.id)}` : '/')} label={mission ? 'Mission' : 'Overview'} />
      <Card className="hero-card">
        <div className="hero-row">
          <div>
            <div className="badge-row">
              <Badge tone={statusTone(session.status)}>{session.status}</Badge>
              <Badge>{session.backend || 'backend unknown'}</Badge>
              {session.model ? <Badge>{session.model}</Badge> : null}
            </div>
            <h2>{displaySessionName(session)}</h2>
            {session.title ? <p className="lead">{session.title}</p> : null}
          </div>
          <div className="date-stack">
            <span>Created {formatDate(session.createdAtUtc)}</span>
            <span>Updated {formatDate(session.updatedAtUtc)}</span>
          </div>
        </div>
        {session.blocker ? (
          <div className="callout danger">
            <AlertTriangle size={18} />
            <span>{session.blocker.summary}</span>
          </div>
        ) : session.finalHandoff ? (
          <div className="callout success">
            <CheckCircle2 size={18} />
            <span>{firstLine(session.finalHandoff.text)}</span>
          </div>
        ) : null}
      </Card>

      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          ['context', 'Context'],
          ['messages', 'Messages'],
          ['handoff', 'Handoff'],
          ['evidence', 'Evidence'],
          ['timeline', 'Timeline']
        ]}
      />

      {tab === 'context' ? (
        <SessionContext session={session} mission={mission} workstream={workstream} navigate={navigate} />
      ) : null}

      {tab === 'messages' ? (
        <MessagesPanel messages={session.messages ?? []} />
      ) : null}

      {tab === 'handoff' ? (
        <HandoffPanel session={session} />
      ) : null}

      {tab === 'evidence' ? (
        <div className="two-column">
          <EvidencePanel evidence={session.evidence ?? []} title="Session Evidence" />
          <VerificationPanel verification={session.verification ?? []} />
        </div>
      ) : null}

      {tab === 'timeline' ? (
        <Timeline items={timeline} title="Status Timeline" />
      ) : null}
    </div>
  );
}

function MissionCard({ bundle, navigate }) {
  const { mission, workstreams = [], sessions = [] } = bundle;
  const visibleSessions = sessions.filter((session) => !isArchived(session.status));
  const latestSession = [...visibleSessions].sort((left, right) => dateValue(right.updatedAtUtc) - dateValue(left.updatedAtUtc))[0];
  return (
    <Card asButton onClick={() => navigate(`/missions/${encodeURIComponent(mission.id)}`)}>
      <div className="card-heading">
        <div>
          <h3>{mission.title}</h3>
          <p className="muted text-tight">{mission.id}</p>
        </div>
        <Badge tone={statusTone(mission.status)}>{mission.status}</Badge>
      </div>
      {mission.intent ? <p className="clamp">{mission.intent}</p> : null}
      {mission.nextAction ? <p className="next-line">{mission.nextAction}</p> : null}
      <div className="mini-stats">
        <span>{workstreams.length} streams</span>
        <span>{visibleSessions.length} sessions</span>
        <span>{(mission.evidence ?? []).length} evidence</span>
      </div>
      {latestSession ? (
        <p className="muted">
          Latest: <strong>{displaySessionName(latestSession)}</strong>
        </p>
      ) : null}
    </Card>
  );
}

function SessionCard({ session, mission, workstream, navigate, detailed = false }) {
  const latestMessage = [...(session.messages ?? [])].sort((left, right) => (right.sequence ?? 0) - (left.sequence ?? 0))[0];
  const latestObservation = [...(session.statusObservations ?? [])].sort((left, right) => dateValue(right.atUtc) - dateValue(left.atUtc))[0];
  return (
    <Card asButton onClick={() => navigate(`/sessions/${encodeURIComponent(session.id)}`)}>
      <div className="card-heading">
        <div>
          <h3>{displaySessionName(session)}</h3>
          <p className="muted text-tight">{session.id}</p>
        </div>
        <Badge tone={statusTone(session.status)}>{session.status}</Badge>
      </div>
      <div className="badge-row">
        {session.backend ? <Badge>{session.backend}</Badge> : null}
        {session.role ? <Badge>{session.role}</Badge> : null}
        {session.model ? <Badge>{session.model}</Badge> : null}
      </div>
      {mission ? <p className="muted">Mission: {mission.title}</p> : null}
      {workstream ? <p className="muted">Stream: {workstream.name}</p> : null}
      {session.blocker ? (
        <InlineAlert tone="danger" icon={AlertTriangle}>{session.blocker.summary}</InlineAlert>
      ) : session.finalHandoff ? (
        <InlineAlert tone="success" icon={CheckCircle2}>{firstLine(session.finalHandoff.text)}</InlineAlert>
      ) : null}
      {detailed && latestMessage?.text ? <MessageExcerpt message={latestMessage} /> : null}
      <div className="mini-stats">
        <span>{(session.messages ?? []).length} messages</span>
        <span>{(session.verification ?? []).length} checks</span>
        <span>{latestObservation?.effectiveStatus ?? 'no status'}</span>
      </div>
    </Card>
  );
}

function WorkstreamCard({ stream }) {
  return (
    <Card>
      <div className="card-heading">
        <div>
          <h3>{stream.name}</h3>
          <p className="muted text-tight">{stream.id}</p>
        </div>
        <Badge tone={statusTone(stream.status)}>{stream.status}</Badge>
      </div>
      <dl className="detail-list">
        <DetailTerm label="Role" value={stream.role} />
        <DetailTerm label="Target" value={stream.target} />
        <DetailTerm label="Clone" value={stream.clonePath} code />
        <DetailTerm label="Source" value={stream.sourceRepoPath} code />
        <DetailTerm label="Branch" value={stream.branch} />
        <DetailTerm label="Depends on" value={(stream.dependsOn ?? []).join(', ')} />
        <DetailTerm label="Integration" value={stream.integrationAction} />
      </dl>
    </Card>
  );
}

function SessionContext({ session, mission, workstream, navigate }) {
  return (
    <div className="two-column">
      <Card>
        <h3>Session Context</h3>
        <dl className="detail-list">
          <DetailTerm label="Display" value={session.displayName} />
          <DetailTerm label="Title" value={session.title} />
          <DetailTerm label="Role" value={session.role} />
          <DetailTerm label="Backend" value={session.backend} />
          <DetailTerm label="Backend ID" value={session.backendSessionId} code />
          <DetailTerm label="Provider" value={session.provider} />
          <DetailTerm label="Model" value={session.model} />
          <DetailTerm label="Variant" value={session.variant} />
          <DetailTerm label="Directory" value={session.directory} code />
        </dl>
      </Card>
      <Card>
        <h3>Map Context</h3>
        {mission ? (
          <button className="plain-link" type="button" onClick={() => navigate(`/missions/${encodeURIComponent(mission.id)}`)}>
            {mission.title}
          </button>
        ) : (
          <p className="muted">No mission record found.</p>
        )}
        {workstream ? (
          <dl className="detail-list spaced">
            <DetailTerm label="Workstream" value={workstream.name} />
            <DetailTerm label="Status" value={workstream.status} />
            <DetailTerm label="Clone" value={workstream.clonePath} code />
            <DetailTerm label="Integration" value={workstream.integrationAction} />
          </dl>
        ) : (
          <p className="muted">No workstream record found.</p>
        )}
      </Card>
    </div>
  );
}

function MessagesPanel({ messages }) {
  const ordered = [...messages].sort((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0));
  return (
    <Section title="Chat Excerpts" aside={`${ordered.length} messages`}>
      {ordered.length === 0 ? (
        <EmptyState title="No messages" body="No message excerpts are stored on this session." />
      ) : (
        <div className="message-stack">
          {ordered.map((message) => (
            <MessageExcerpt key={`${message.id}:${message.partId ?? ''}:${message.sequence}`} message={message} expanded />
          ))}
        </div>
      )}
    </Section>
  );
}

function HandoffPanel({ session }) {
  return (
    <div className="two-column">
      <Card>
        <div className="section-title compact">
          <FileText size={18} />
          <h3>Final Handoff</h3>
        </div>
        {session.finalHandoff ? (
          <>
            <p className="muted">Recorded {formatDate(session.finalHandoff.atUtc)}</p>
            <pre className="text-block">{session.finalHandoff.text}</pre>
          </>
        ) : (
          <EmptyState title="No handoff" body="No final handoff is stored on this session." />
        )}
      </Card>
      <Card>
        <div className="section-title compact">
          <AlertTriangle size={18} />
          <h3>Blocker</h3>
        </div>
        {session.blocker ? (
          <>
            <p className="muted">Recorded {formatDate(session.blocker.atUtc)}</p>
            <p>{session.blocker.summary}</p>
            {session.blocker.evidence ? <pre className="text-block">{session.blocker.evidence}</pre> : null}
          </>
        ) : (
          <EmptyState title="No blocker" body="No blocker is stored on this session." />
        )}
      </Card>
    </div>
  );
}

function EvidencePanel({ evidence, title }) {
  return (
    <Section title={title} aside={`${evidence.length} records`}>
      {evidence.length === 0 ? (
        <EmptyState title="No evidence" body="No evidence records are stored here." />
      ) : (
        <div className="card-grid">
          {evidence.map((item) => (
            <Card key={item.id}>
              <div className="card-heading">
                <div>
                  <h3>{item.kind || 'Evidence'}</h3>
                  <p className="muted text-tight">{item.id}</p>
                </div>
                <Badge>{formatDate(item.addedAtUtc)}</Badge>
              </div>
              {item.summary ? <p>{item.summary}</p> : null}
              {item.path ? <code className="path-code">{item.path}</code> : null}
            </Card>
          ))}
        </div>
      )}
    </Section>
  );
}

function VerificationPanel({ verification }) {
  return (
    <Section title="Verification" aside={`${verification.length} checks`}>
      {verification.length === 0 ? (
        <EmptyState title="No verification" body="No verification results are stored on this session." />
      ) : (
        <div className="card-grid single">
          {verification.map((item, index) => (
            <Card key={`${item.kind}:${item.atUtc}:${index}`}>
              <div className="card-heading">
                <div>
                  <h3>{item.kind}</h3>
                  <p className="muted text-tight">{formatDate(item.atUtc)}</p>
                </div>
                <Badge tone={statusTone(item.result)}>{item.result}</Badge>
              </div>
              {item.summary ? <p>{item.summary}</p> : null}
            </Card>
          ))}
        </div>
      )}
    </Section>
  );
}

function Timeline({ items, title }) {
  return (
    <Section title={title} aside={`${items.length} events`}>
      {items.length === 0 ? (
        <EmptyState title="No timeline" body="No events or status observations are stored yet." />
      ) : (
        <ol className="timeline">
          {items.map((item, index) => (
            <li key={`${item.atUtc}:${item.type}:${index}`}>
              <div className="timeline-dot" />
              <div>
                <div className="timeline-head">
                  <strong>{item.type || 'event'}</strong>
                  <span>{formatDate(item.atUtc)}</span>
                </div>
                {item.summary ? <p>{item.summary}</p> : null}
              </div>
            </li>
          ))}
        </ol>
      )}
    </Section>
  );
}

function StatCard({ label, value, icon: Icon, compact = false }) {
  return (
    <Card className={cx('stat-card', compact && 'compact-stat')}>
      <div className="stat-icon"><Icon size={18} /></div>
      <div>
        <p className="muted">{label}</p>
        <strong>{value}</strong>
      </div>
    </Card>
  );
}

function Section({ title, aside, children }) {
  return (
    <section className="content-section">
      <div className="section-title">
        <h2>{title}</h2>
        {aside ? <span>{aside}</span> : null}
      </div>
      {children}
    </section>
  );
}

function InlineAlert({ children, icon: Icon, tone }) {
  return (
    <div className={cx('inline-alert', tone)}>
      <Icon size={16} />
      <span>{children}</span>
    </div>
  );
}

function MessageExcerpt({ message, expanded = false }) {
  return (
    <article className={cx('message-excerpt', expanded && 'expanded')}>
      <header>
        <div className="badge-row">
          <Badge tone={message.role === 'assistant' ? 'info' : 'neutral'}>{message.role || 'message'}</Badge>
          {message.isExcerpt ? <Badge tone="warning">excerpt</Badge> : null}
        </div>
        <span>{formatDate(message.timestamp)}</span>
      </header>
      <pre>{message.text || '(empty)'}</pre>
    </article>
  );
}

function DetailTerm({ label, value, code = false }) {
  if (!value) return null;
  return (
    <>
      <dt>{label}</dt>
      <dd>{code ? <code>{value}</code> : value}</dd>
    </>
  );
}

function BackButton({ onClick, label }) {
  return (
    <Button variant="ghost" size="fit" type="button" onClick={onClick}>
      <ArrowLeft size={16} />
      <span>{label}</span>
    </Button>
  );
}

function EmptyState({ title, body }) {
  return (
    <div className="empty-state">
      <FileText size={22} />
      <div>
        <strong>{title}</strong>
        <p>{body}</p>
      </div>
    </div>
  );
}

function ErrorState({ error, onRefresh }) {
  return (
    <main>
      <Card className="error-card">
        <div className="section-title compact">
          <AlertTriangle size={20} />
          <h2>Observer Error</h2>
        </div>
        <p>{error.message}</p>
        <Button type="button" onClick={onRefresh}>
          <RefreshCw size={16} />
          <span>Try again</span>
        </Button>
      </Card>
    </main>
  );
}

function LoadingState() {
  return (
    <main>
      <div className="loading-row">
        <RefreshCw size={20} className="spin" />
        <span>Loading work-map records</span>
      </div>
    </main>
  );
}

function readRoute() {
  const parts = window.location.pathname.split('/').filter(Boolean).map((part) => decodeURIComponent(part));
  if (parts[0] === 'missions' && parts[1]) return { kind: 'mission', id: parts[1] };
  if (parts[0] === 'sessions' && parts[1]) return { kind: 'session', id: parts[1] };
  return { kind: 'overview' };
}

function routeToApi(route) {
  if (route.kind === 'mission') return `/api/missions/${encodeURIComponent(route.id)}`;
  if (route.kind === 'session') return `/api/sessions/${encodeURIComponent(route.id)}`;
  return '/api/missions';
}

async function fetchJson(path) {
  const response = await fetch(path, { cache: 'no-store' });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Request failed with ${response.status}`);
  }

  return response.json();
}

function displaySessionName(session) {
  return session.displayName || session.title || session.id;
}

function statusTone(status) {
  const value = normalize(status);
  if (['handoff', 'done', 'complete', 'pass', 'passed'].includes(value)) return 'success';
  if (['blocked', 'failed', 'fail', 'error'].includes(value)) return 'danger';
  if (['waiting', 'queued', 'running', 'in-progress', 'needs-review'].includes(value)) return 'info';
  if (['planned', 'linked', 'skip', 'skipped', 'archived'].includes(value)) return 'warning';
  return 'neutral';
}

function normalize(value) {
  return String(value || '').trim().toLowerCase();
}

function isArchived(status) {
  return ['archived', 'archive'].includes(normalize(status));
}

function firstLine(value) {
  return String(value || '').split(/\r?\n/).find(Boolean) || '';
}

function formatDate(value) {
  if (!value) return 'not recorded';
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) return String(value);
  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(date);
}

function formatTime(value) {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  }).format(value);
}

function dateValue(value) {
  if (!value) return 0;
  const time = new Date(value).getTime();
  return Number.isNaN(time) ? 0 : time;
}

function shortPath(path) {
  if (!path) return 'unknown';
  const parts = String(path).split(/[\\/]/).filter(Boolean);
  if (parts.length <= 2) return path;
  return `${parts.at(-2)}/${parts.at(-1)}`;
}

function cx(...classes) {
  return classes.filter(Boolean).join(' ');
}

createRoot(document.getElementById('root')).render(<App />);
