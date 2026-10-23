import { useRef, useState, type FormEvent } from 'react'
import { ApiError, apiRequest } from '../../app/apiClient'

export type ItemStatus = 'Outstanding' | 'Uploaded' | 'Approved' | 'Rejected'

// Matches GET /api/portal/request in docs/api-contract.md.
export interface PortalItem {
  id: string
  title: string
  description: string | null
  required: boolean
  status: ItemStatus
  rejectionComment?: string
  latestUpload?: { originalName: string; uploadedAt: string } | null
}

// Matches POST /api/portal/items/{itemId}/files.
interface UploadedFile {
  id: string
  originalName: string
  sizeBytes: number
  uploadedAt: string
}

// A symbol as well as a word, so the status never relies on colour alone (S9.7).
const STATUS_LABELS: Record<ItemStatus, string> = {
  Outstanding: '○ Outstanding',
  Uploaded: '↑ Uploaded, awaiting review',
  Approved: '✓ Approved',
  Rejected: '✕ Rejected, please upload again',
}

// Used only when the server sent no `detail`. A status code is never shown (S9.8).
const FALLBACKS: Record<number, string> = {
  409: 'This document was already approved, so it cannot be replaced.',
  413: 'That file is too large. The limit is 20 MB.',
  415: 'That file type is not allowed. Use PDF, JPG, PNG, DOCX or XLSX.',
}

function readableMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return (
      error.problem.detail ?? FALLBACKS[error.status] ?? 'The upload did not work. Please try again.'
    )
  }
  return 'Could not reach the server. Check your connection and try again.'
}

interface UploadCardProps {
  item: PortalItem
  sessionToken: string
  onUploaded: () => void
  onSessionExpired: () => void
}

export function UploadCard({ item, sessionToken, onUploaded, onSessionExpired }: UploadCardProps) {
  const formRef = useRef<HTMLFormElement>(null)
  const inFlight = useRef(false)
  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')

  const inputId = `file-${item.id}`
  const canUpload = item.status !== 'Approved'

  async function submit(event: FormEvent) {
    event.preventDefault()
    // The ref, not the state, guards against a second submit before React re-renders.
    if (file === null || inFlight.current) return
    inFlight.current = true
    setUploading(true)
    setMessage('')
    setError('')

    const body = new FormData()
    body.append('file', file)
    try {
      const uploaded = await apiRequest<UploadedFile>(`/api/portal/items/${item.id}/files`, {
        method: 'POST',
        body,
        token: sessionToken,
        scheme: 'Portal',
      })
      setMessage(`Uploaded ${uploaded.originalName}. It is now waiting for review.`)
      setFile(null)
      formRef.current?.reset()
      onUploaded()
    } catch (caught) {
      if (caught instanceof ApiError && caught.status === 401) {
        onSessionExpired()
        return
      }
      setError(readableMessage(caught))
    } finally {
      inFlight.current = false
      setUploading(false)
    }
  }

  return (
    <li className={`portal-item portal-item--${item.status.toLowerCase()}`}>
      <h2>{item.title}</h2>
      <p>
        <strong>{STATUS_LABELS[item.status]}</strong>
        {item.required ? ' · Required' : ' · Optional'}
      </p>
      {item.description && <p>{item.description}</p>}
      {item.status === 'Rejected' && item.rejectionComment && (
        <p>
          <strong>Reason:</strong> {item.rejectionComment}
        </p>
      )}
      {item.latestUpload && <p className="hint">Last file: {item.latestUpload.originalName}</p>}

      {canUpload && (
        <form ref={formRef} onSubmit={submit}>
          <label htmlFor={inputId}>Choose a file for {item.title}</label>
          <input
            id={inputId}
            type="file"
            accept=".pdf,.jpg,.jpeg,.png,.docx,.xlsx"
            disabled={uploading}
            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          />
          <button type="submit" disabled={uploading || file === null}>
            {uploading ? 'Uploading…' : 'Upload'}
          </button>
        </form>
      )}

      <p role="status">{message}</p>
      {error !== '' && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
    </li>
  )
}
