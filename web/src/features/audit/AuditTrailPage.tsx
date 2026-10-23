import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useApi } from '../../app/useApi'

// Matches GET /api/requests/{id}/audit in docs/api-contract.md.
interface AuditEvent {
  id: string
  occurredAt: string
  actorType: string
  actorId: string | null
  action: string
  entityType: string
  entityId: string | null
  metadata: Record<string, unknown> | null
}

interface AuditPage {
  items: AuditEvent[]
  page: number
  pageSize: number
  total: number
}

const PAGE_SIZE = 50

// Timestamps arrive as UTC and are shown as UTC, so the trail reads the same for every viewer.
function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString(undefined, {
    timeZone: 'UTC',
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    timeZoneName: 'short',
  })
}

function formatMetadata(metadata: AuditEvent['metadata']): string {
  return metadata === null
    ? ''
    : Object.entries(metadata)
        .map(([key, value]) => `${key}: ${typeof value === 'string' ? value : JSON.stringify(value)}`)
        .join(', ')
}

export function AuditTrailPage() {
  const { id = '' } = useParams()
  const [page, setPage] = useState(1)

  const query = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) })
  const { data, error, loading, refetch } = useApi<AuditPage>(
    `/api/requests/${encodeURIComponent(id)}/audit?${query}`,
  )

  const pageCount = data === null ? 1 : Math.max(1, Math.ceil(data.total / data.pageSize))

  return (
    <main className="page">
      <h1>Audit trail</h1>

      <p>
        <Link to="/requests">Back to requests</Link>
      </p>

      {error !== null ? (
        <div role="alert" className="error">
          <p>{error.message}</p>
          <button type="button" onClick={refetch}>
            Try again
          </button>
        </div>
      ) : loading || data === null ? (
        <p role="status">Loading audit trail…</p>
      ) : data.items.length === 0 ? (
        <p role="status">No audit events for this request yet.</p>
      ) : (
        <>
          <table>
            <caption className="hint">
              {data.total} {data.total === 1 ? 'event' : 'events'}, newest first
            </caption>
            <thead>
              <tr>
                <th scope="col">When (UTC)</th>
                <th scope="col">Action</th>
                <th scope="col">Actor</th>
                <th scope="col">Entity</th>
                <th scope="col">Details</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((event) => (
                <tr key={event.id}>
                  <td>
                    <time dateTime={event.occurredAt}>{formatTimestamp(event.occurredAt)}</time>
                  </td>
                  <td>{event.action}</td>
                  <td>{event.actorType}</td>
                  <td>{event.entityType}</td>
                  <td>{formatMetadata(event.metadata)}</td>
                </tr>
              ))}
            </tbody>
          </table>

          <nav aria-label="Pagination">
            <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>
              Previous
            </button>{' '}
            <span>
              Page {data.page} of {pageCount}
            </span>{' '}
            <button type="button" disabled={page >= pageCount} onClick={() => setPage(page + 1)}>
              Next
            </button>
          </nav>
        </>
      )}
    </main>
  )
}
