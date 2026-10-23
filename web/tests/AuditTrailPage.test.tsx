import { describe, expect, it, vi, afterEach } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { AuthProvider } from '../src/app/AuthContext'
import { AuditTrailPage } from '../src/features/audit/AuditTrailPage'
import * as apiClient from '../src/app/apiClient'

const REQUEST_ID = 'a3f1c2d4-0000-4000-8000-000000000001'

function event(overrides: Record<string, unknown> = {}) {
  return {
    id: '9d10c2d4-0000-4000-8000-000000000001',
    occurredAt: '2030-03-15T11:02:00Z',
    actorType: 'Client',
    actorId: REQUEST_ID,
    action: 'ItemUploaded',
    entityType: 'ChecklistItem',
    entityId: 'c91d0000-0000-4000-8000-000000000001',
    metadata: { originalName: 'passport.pdf', sizeBytes: 184320 },
    ...overrides,
  }
}

function page(items: unknown[], total = items.length, pageSize = 50) {
  return { items, page: 1, pageSize, total }
}

function mockAudit(response: unknown) {
  return vi.spyOn(apiClient, 'apiRequest').mockResolvedValue(response)
}

function renderTrail() {
  return render(
    <MemoryRouter initialEntries={[`/requests/${REQUEST_ID}/audit`]}>
      <AuthProvider>
        <Routes>
          <Route path="/requests/:id/audit" element={<AuditTrailPage />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  )
}

describe('AuditTrailPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows a loading state until the trail arrives', async () => {
    mockAudit(page([event()]))

    renderTrail()

    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)
    expect(await screen.findByRole('table')).toBeInTheDocument()
    expect(screen.queryByText(/loading/i)).not.toBeInTheDocument()
  })

  it('asks for this request’s trail by the id in the URL', async () => {
    const spy = mockAudit(page([event()]))

    renderTrail()
    await screen.findByRole('table')

    const path = spy.mock.calls[0]![0]
    expect(path.startsWith(`/api/requests/${REQUEST_ID}/audit?`)).toBe(true)
  })

  it('shows each event with its action, actor, entity and metadata', async () => {
    mockAudit(page([event()]))

    renderTrail()

    const cell = await screen.findByRole('cell', { name: 'ItemUploaded' })
    const row = cell.closest('tr')!
    expect(row).toHaveTextContent('Client')
    expect(row).toHaveTextContent('ChecklistItem')
    expect(row).toHaveTextContent('passport.pdf')
    expect(row).toHaveTextContent('184320')
  })

  it('shows an event with no metadata without crashing', async () => {
    mockAudit(page([event({ metadata: null, action: 'RequestSent' })]))

    renderTrail()

    expect(await screen.findByRole('cell', { name: 'RequestSent' })).toBeInTheDocument()
  })

  it('shows an empty state when the request has no events', async () => {
    mockAudit(page([]))

    renderTrail()

    expect(await screen.findByText(/no audit events/i)).toBeInTheDocument()
  })

  it('shows the server message on failure and retries on request', async () => {
    const spy = vi
      .spyOn(apiClient, 'apiRequest')
      .mockRejectedValueOnce(
        new apiClient.ApiError(404, { status: 404, title: 'Not found.' }),
      )
      .mockResolvedValue(page([event()]))

    renderTrail()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Not found.')
    expect(alert).not.toHaveTextContent('404')

    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('table')).toBeInTheDocument()
    expect(spy).toHaveBeenCalledTimes(2)
  })

  it('pages through the trail', async () => {
    const spy = mockAudit(page([event()], 120, 50))

    renderTrail()
    expect(await screen.findByText('Page 1 of 3')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Next' }))

    const params = new URL(spy.mock.calls.at(-1)![0], 'http://api.test').searchParams
    expect(params.get('page')).toBe('2')
  })

  it('does not call fetch itself', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch')
    mockAudit(page([event()]))

    renderTrail()
    await screen.findByRole('table')

    expect(fetchSpy).not.toHaveBeenCalled()
  })
})
