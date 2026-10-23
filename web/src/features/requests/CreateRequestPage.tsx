import { useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useApiMutation } from '../../app/useApi'

// Matches POST /api/requests in docs/api-contract.md.
interface CreateRequestBody {
  clientName: string
  clientEmail: string
  dueDate: string
  items: { title: string; description: string | null; required: boolean }[]
}

interface CreateRequestResponse {
  id: string
}

interface ItemDraft {
  key: number
  title: string
  description: string
  required: boolean
}

export function CreateRequestPage() {
  const navigate = useNavigate()
  const { run, error, loading } = useApiMutation<CreateRequestBody, CreateRequestResponse>(
    '/api/requests',
  )

  const [clientName, setClientName] = useState('')
  const [clientEmail, setClientEmail] = useState('')
  const [dueDate, setDueDate] = useState('')

  // Items need a stable key of their own: an index would reattach the wrong input's state when
  // an earlier item is removed.
  const nextKey = useRef(1)
  const [items, setItems] = useState<ItemDraft[]>([
    { key: 0, title: '', description: '', required: true },
  ])

  const hasFieldErrors = error?.problem.errors !== undefined

  function updateItem(key: number, changes: Partial<ItemDraft>) {
    setItems((current) => current.map((item) => (item.key === key ? { ...item, ...changes } : item)))
  }

  function addItem() {
    setItems((current) => [
      ...current,
      { key: nextKey.current++, title: '', description: '', required: true },
    ])
  }

  function removeItem(key: number) {
    setItems((current) => current.filter((item) => item.key !== key))
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()

    const created = await run({
      clientName,
      clientEmail,
      dueDate,
      items: items.map((item) => ({
        title: item.title,
        description: item.description === '' ? null : item.description,
        required: item.required,
      })),
    })

    if (created !== null) {
      await navigate('/requests')
    }
  }

  // The server's wording for a field, shown beside it and announced when it appears.
  function fieldError(field: string, id: string) {
    const messages = error?.fieldErrors(field) ?? []
    if (messages.length === 0) return null
    return (
      <p id={id} role="alert" className="error">
        {messages.join(' ')}
      </p>
    )
  }

  function describedBy(field: string, id: string) {
    return (error?.fieldErrors(field).length ?? 0) > 0 ? id : undefined
  }

  return (
    <main className="page page--narrow">
      <h1>New request</h1>

      <form onSubmit={handleSubmit} noValidate>
        <label htmlFor="clientName">Client name</label>
        <input
          id="clientName"
          name="clientName"
          value={clientName}
          onChange={(e) => setClientName(e.target.value)}
          aria-describedby={describedBy('clientName', 'clientName-error')}
          required
        />
        {fieldError('clientName', 'clientName-error')}

        <label htmlFor="clientEmail">Client email</label>
        <input
          id="clientEmail"
          name="clientEmail"
          type="email"
          value={clientEmail}
          onChange={(e) => setClientEmail(e.target.value)}
          aria-describedby={describedBy('clientEmail', 'clientEmail-error')}
          required
        />
        {fieldError('clientEmail', 'clientEmail-error')}

        <label htmlFor="dueDate">Due date</label>
        <input
          id="dueDate"
          name="dueDate"
          type="date"
          value={dueDate}
          onChange={(e) => setDueDate(e.target.value)}
          aria-describedby={describedBy('dueDate', 'dueDate-error')}
          required
        />
        {fieldError('dueDate', 'dueDate-error')}

        <fieldset>
          <legend>Documents to request</legend>
          {fieldError('items', 'items-error')}

          {items.map((item, index) => (
            <div key={item.key} role="group" aria-label={`Document ${index + 1}`}>
              <label htmlFor={`item-${item.key}-title`}>Document {index + 1} title</label>
              <input
                id={`item-${item.key}-title`}
                value={item.title}
                onChange={(e) => updateItem(item.key, { title: e.target.value })}
                aria-describedby={describedBy(`items[${index}].title`, `item-${item.key}-title-error`)}
              />
              {fieldError(`items[${index}].title`, `item-${item.key}-title-error`)}

              <label htmlFor={`item-${item.key}-description`}>
                Document {index + 1} description (optional)
              </label>
              <input
                id={`item-${item.key}-description`}
                value={item.description}
                onChange={(e) => updateItem(item.key, { description: e.target.value })}
                aria-describedby={describedBy(
                  `items[${index}].description`,
                  `item-${item.key}-description-error`,
                )}
              />
              {fieldError(`items[${index}].description`, `item-${item.key}-description-error`)}

              <label htmlFor={`item-${item.key}-required`}>
                <input
                  id={`item-${item.key}-required`}
                  type="checkbox"
                  checked={item.required}
                  onChange={(e) => updateItem(item.key, { required: e.target.checked })}
                />{' '}
                Document {index + 1} is required
              </label>

              <button type="button" onClick={() => removeItem(item.key)}>
                Remove document {index + 1}
              </button>
            </div>
          ))}

          <button type="button" onClick={addItem}>
            Add document
          </button>
        </fieldset>

        {/* A failure with no per-field messages (server error, network) still has to be said. */}
        {error !== null && !hasFieldErrors && (
          <p role="alert" className="error">
            {error.message}
          </p>
        )}

        <button type="submit" disabled={loading}>
          {loading ? 'Creating…' : 'Create request'}
        </button>
      </form>

      <p className="hint">
        <Link to="/requests">Back to requests</Link>
      </p>
    </main>
  )
}
