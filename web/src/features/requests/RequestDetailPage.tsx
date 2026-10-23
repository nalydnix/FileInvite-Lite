import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useApi, useApiMutation } from '../../app/useApi'

type RequestStatus = 'Draft' | 'Sent' | 'InProgress' | 'Complete'
type ItemStatus = 'Outstanding' | 'Uploaded' | 'Approved' | 'Rejected'

// Matches GET /api/requests/{id} in docs/api-contract.md.
interface RequestDetail {
  id: string
  clientName: string
  clientEmail: string
  dueDate: string
  status: RequestStatus
  isOverdue: boolean
  createdAt: string
  createdBy: { id: string; name: string } | null
  /** Metadata only. The token and the URL are never returned by a read (S1.12). */
  magicLink: { issuedAt: string; expiresAt: string; revoked: boolean } | null
  items: {
    id: string
    title: string
    description: string | null
    required: boolean
    status: ItemStatus
    latestFile: {
      id: string
      originalName: string
      contentType: string
      sizeBytes: number
      uploadedAt: string
    } | null
    latestDecision: { decision: string; comment: string | null; reviewedAt: string } | null
  }[]
}

// Matches POST /api/requests/{id}/send. The only response in the API that carries a token.
interface SendResponse {
  url: string
  expiresAt: string
}

const STATUS_LABELS: Record<RequestStatus, string> = {
  Draft: 'Draft',
  Sent: 'Sent',
  InProgress: 'In progress',
  Complete: 'Complete',
}

// A due date is a calendar date, so it is formatted in UTC and must not shift with the time zone.
function formatDate(value: string): string {
  return new Date(`${value.slice(0, 10)}T00:00:00Z`).toLocaleDateString(undefined, {
    timeZone: 'UTC',
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

function formatInstant(value: string): string {
  return new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export function RequestDetailPage() {
  const { id = '' } = useParams()
  const { data, error, loading, refetch } = useApi<RequestDetail>(`/api/requests/${id}`)
  const send = useApiMutation<undefined, SendResponse>(`/api/requests/${id}/send`)

  // The link lives in this component's state and nowhere else: not storage, not the URL, not a
  // cache. Leaving the page, or sending again, discards it — which is what "shown once" means.
  const [issued, setIssued] = useState<SendResponse | null>(null)
  const [copyMessage, setCopyMessage] = useState<string | null>(null)

  async function handleSend() {
    setCopyMessage(null)
    const result = await send.run(undefined)
    if (result !== null) {
      setIssued(result)
      refetch()
    }
  }

  async function handleCopy() {
    if (issued === null) return
    try {
      await navigator.clipboard.writeText(issued.url)
      setCopyMessage('Link copied.')
    } catch {
      setCopyMessage('Could not copy automatically. Select the link and copy it yourself.')
    }
  }

  if (error !== null) {
    return (
      <main className="page">
        <p>
          <Link to="/requests">Back to requests</Link>
        </p>
        <div role="alert" className="error">
          <p>{error.message}</p>
          <button type="button" onClick={refetch}>
            Try again
          </button>
        </div>
      </main>
    )
  }

  if (loading && data === null) {
    return (
      <main className="page">
        <p role="status">Loading request…</p>
      </main>
    )
  }

  if (data === null) return null

  const hasLiveLink = data.magicLink !== null && !data.magicLink.revoked
  const sendLabel = hasLiveLink ? 'Send again' : 'Send request'

  return (
    <main className="page">
      <p>
        <Link to="/requests">Back to requests</Link>
      </p>

      <h1>{data.clientName}</h1>
      <p>
        {data.clientEmail} · Due {formatDate(data.dueDate)}
        {/* Text, not just colour: an overdue request must be identifiable without it. */}
        {data.isOverdue && <strong> (Overdue)</strong>} · {STATUS_LABELS[data.status]}
      </p>

      <section aria-labelledby="link-heading">
        <h2 id="link-heading">Client link</h2>

        {data.magicLink === null ? (
          <p>No link has been sent yet.</p>
        ) : (
          <p>
            Issued {formatInstant(data.magicLink.issuedAt)}.{' '}
            {data.magicLink.revoked
              ? 'This link has been revoked.'
              : `Expires ${formatInstant(data.magicLink.expiresAt)}.`}
          </p>
        )}

        {hasLiveLink && (
          <p className="hint">
            Sending again revokes the current link, so anything the client already has stops working.
          </p>
        )}

        <button type="button" onClick={handleSend} disabled={send.loading}>
          {send.loading ? 'Sending…' : sendLabel}
        </button>

        {send.error !== null && (
          <div role="alert" className="error">
            <p>{send.error.message}</p>
          </div>
        )}

        {issued !== null && (
          <div role="region" aria-label="New client link">
            <p>
              <strong>This link will not be shown again.</strong> Copy it now and send it to the
              client. If you lose it, send the request again to get a new one.
            </p>
            <label htmlFor="magic-link">Link to send to the client</label>
            <input
              id="magic-link"
              readOnly
              value={issued.url}
              onFocus={(e) => e.target.select()}
            />
            <p>
              <button type="button" onClick={handleCopy}>
                Copy link
              </button>{' '}
              <span className="hint">Expires {formatInstant(issued.expiresAt)}.</span>
            </p>
            {copyMessage !== null && <p role="status">{copyMessage}</p>}
          </div>
        )}
      </section>

      <section aria-labelledby="items-heading">
        <h2 id="items-heading">Documents</h2>
        {data.items.length === 0 ? (
          <p role="status">This request has no documents.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th scope="col">Document</th>
                <th scope="col">Required</th>
                <th scope="col">Status</th>
                <th scope="col">Latest upload</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((item) => (
                <tr key={item.id}>
                  <th scope="row">
                    {item.title}
                    {item.description !== null && <div className="hint">{item.description}</div>}
                  </th>
                  <td>{item.required ? 'Yes' : 'No'}</td>
                  <td>{item.status}</td>
                  <td>
                    {item.latestFile === null
                      ? 'Nothing uploaded'
                      : `${item.latestFile.originalName}, ${formatInstant(item.latestFile.uploadedAt)}`}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </main>
  )
}
