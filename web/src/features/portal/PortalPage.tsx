import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, apiRequest } from '../../app/apiClient'
import { readPortalToken, usePortalSession } from '../../app/portalSessionState'
import { UploadCard, type PortalItem } from './UploadCard'

// Matches POST /api/portal/exchange and GET /api/portal/request in docs/api-contract.md.
interface ExchangeResult {
  sessionToken: string
  expiresAt: string
  requestId: string
}

interface PortalRequestView {
  clientName: string
  dueDate: string
  isOverdue: boolean
  items: PortalItem[]
}

type Exchange = 'working' | 'failed' | 'done'

// Formatted in UTC: a due date is a calendar date and must not shift with the viewer's time zone.
function formatDate(value: string): string {
  return new Date(`${value.slice(0, 10)}T00:00:00Z`).toLocaleDateString(undefined, {
    timeZone: 'UTC',
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

function NewLinkMessage() {
  return (
    <main className="page">
      <h1>This link cannot be used</h1>
      <p role="alert">
        This link is missing, has expired or is no longer valid. Please ask whoever sent you this
        for a new link.
      </p>
    </main>
  )
}

export function PortalPage() {
  const { sessionToken, start, end } = usePortalSession()
  const [exchange, setExchange] = useState<Exchange>('working')
  const started = useRef(false)

  // The link token is exchanged exactly once. The ref survives StrictMode's second effect run,
  // so the single-use token is never sent twice (S9.3).
  useEffect(() => {
    if (started.current) return
    started.current = true

    const token = readPortalToken(window.location.hash)
    if (token === null) {
      setExchange('failed')
      return
    }

    // Strip the fragment before anything else can navigate, log or copy the URL (S9.3).
    window.history.replaceState(null, '', window.location.pathname + window.location.search)

    apiRequest<ExchangeResult>('/api/portal/exchange', { method: 'POST', body: { token } })
      .then((result) => {
        start(result.sessionToken, result.expiresAt)
        setExchange('done')
      })
      .catch(() => setExchange('failed'))
  }, [start])

  const expire = useCallback(() => {
    end()
    setExchange('failed')
  }, [end])

  if (exchange === 'failed') return <NewLinkMessage />
  if (exchange === 'working' || sessionToken === null) {
    return (
      <main className="page">
        <p role="status">Loading your documents…</p>
      </main>
    )
  }
  return <RequestView sessionToken={sessionToken} onSessionExpired={expire} />
}

function RequestView({
  sessionToken,
  onSessionExpired,
}: {
  sessionToken: string
  onSessionExpired: () => void
}) {
  const [data, setData] = useState<PortalRequestView | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setError(null)

    apiRequest<PortalRequestView>('/api/portal/request', {
      token: sessionToken,
      scheme: 'Portal',
      signal: controller.signal,
    })
      .then(setData)
      .catch((caught: unknown) => {
        if (controller.signal.aborted) return
        if (caught instanceof ApiError && caught.status === 401) {
          onSessionExpired()
          return
        }
        setError(
          caught instanceof ApiError
            ? caught
            : new ApiError(0, { title: 'Could not reach the server. Check your connection.' }),
        )
      })

    return () => controller.abort()
  }, [sessionToken, attempt, onSessionExpired])

  const refetch = useCallback(() => setAttempt((count) => count + 1), [])

  return (
    <main className="page">
      <h1>Your documents</h1>

      {error !== null ? (
        <div role="alert" className="error">
          <p>{error.problem.detail ?? error.problem.title ?? 'Something went wrong.'}</p>
          <button type="button" onClick={refetch}>
            Try again
          </button>
        </div>
      ) : data === null ? (
        <p role="status">Loading your documents…</p>
      ) : (
        <>
          <p>
            Hello {data.clientName}. Please upload the documents below by{' '}
            {formatDate(data.dueDate)}
            {data.isOverdue && <strong> (Overdue)</strong>}.
          </p>
          {data.items.length === 0 ? (
            <p role="status">There is nothing to upload right now.</p>
          ) : (
            <ul className="portal-items">
              {data.items.map((item) => (
                <UploadCard
                  key={item.id}
                  item={item}
                  sessionToken={sessionToken}
                  onUploaded={refetch}
                  onSessionExpired={onSessionExpired}
                />
              ))}
            </ul>
          )}
        </>
      )}
    </main>
  )
}
