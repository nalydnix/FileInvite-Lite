import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useApi } from '../../app/useApi'

type RequestStatus = 'Draft' | 'Sent' | 'InProgress' | 'Complete'
type SortKey = 'dueDate' | 'createdAt'

// Matches GET /api/requests in docs/api-contract.md.
interface RequestRow {
  id: string
  clientName: string
  dueDate: string
  status: RequestStatus
  itemCount: number
  approvedCount: number
  isOverdue: boolean
  createdAt: string
}

interface RequestPage {
  items: RequestRow[]
  page: number
  pageSize: number
  total: number
}

const PAGE_SIZE = 20

const STATUS_LABELS: Record<RequestStatus, string> = {
  Draft: 'Draft',
  Sent: 'Sent',
  InProgress: 'In progress',
  Complete: 'Complete',
}

// Formatted in UTC: a due date is a calendar date and must not shift with the viewer's time zone.
function formatDate(value: string): string {
  return new Date(`${value.slice(0, 10)}T00:00:00Z`).toLocaleDateString(undefined, {
    timeZone: 'UTC',
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

export function RequestListPage() {
  const [status, setStatus] = useState<RequestStatus | ''>('')
  const [overdueOnly, setOverdueOnly] = useState(false)
  const [sort, setSort] = useState<SortKey>('dueDate')
  const [page, setPage] = useState(1)

  const query = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE), sort })
  if (status !== '') query.set('status', status)
  if (overdueOnly) query.set('overdue', 'true')

  const { data, error, loading, refetch } = useApi<RequestPage>(`/api/requests?${query}`)

  const filtered = status !== '' || overdueOnly
  const pageCount = data === null ? 1 : Math.max(1, Math.ceil(data.total / data.pageSize))

  // Changing a filter returns to the first page: the old page number may not exist any more.
  function changeFilter(apply: () => void) {
    apply()
    setPage(1)
  }

  return (
    <main className="page">
      <h1>Requests</h1>

      <p>
        <Link to="/requests/new">New request</Link>
      </p>

      <form onSubmit={(event) => event.preventDefault()} aria-label="Filter requests">
        <label htmlFor="status">Status</label>
        <select
          id="status"
          value={status}
          onChange={(e) => changeFilter(() => setStatus(e.target.value as RequestStatus | ''))}
        >
          <option value="">All</option>
          {Object.entries(STATUS_LABELS).map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </select>

        <label htmlFor="sort">Sort by</label>
        <select
          id="sort"
          value={sort}
          onChange={(e) => changeFilter(() => setSort(e.target.value as SortKey))}
        >
          <option value="dueDate">Due date</option>
          <option value="createdAt">Created</option>
        </select>

        <label htmlFor="overdue">
          <input
            id="overdue"
            type="checkbox"
            checked={overdueOnly}
            onChange={(e) => changeFilter(() => setOverdueOnly(e.target.checked))}
          />{' '}
          Overdue only
        </label>
      </form>

      {error !== null ? (
        <div role="alert" className="error">
          <p>{error.message}</p>
          <button type="button" onClick={refetch}>
            Try again
          </button>
        </div>
      ) : loading || data === null ? (
        <p role="status">Loading requests…</p>
      ) : data.items.length === 0 ? (
        <p role="status">
          {filtered
            ? 'No requests match these filters.'
            : 'No requests yet. Create the first one with “New request”.'}
        </p>
      ) : (
        <>
          <table>
            <caption className="hint">
              {data.total} {data.total === 1 ? 'request' : 'requests'}
            </caption>
            <thead>
              <tr>
                <th scope="col">Client</th>
                <th scope="col">Due date</th>
                <th scope="col">Status</th>
                <th scope="col">Approved</th>
                <th scope="col">Created</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((row) => (
                <tr key={row.id}>
                  <th scope="row">{row.clientName}</th>
                  <td>
                    {formatDate(row.dueDate)}
                    {/* Text, not just colour: an overdue row must be identifiable without it. */}
                    {row.isOverdue && <strong> (Overdue)</strong>}
                  </td>
                  <td>{STATUS_LABELS[row.status]}</td>
                  <td>
                    {row.approvedCount} of {row.itemCount}
                  </td>
                  <td>{formatDate(row.createdAt)}</td>
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
