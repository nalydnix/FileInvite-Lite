import { describe, expect, it, vi, afterEach } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { AuthProvider } from '../src/app/AuthContext'
import { RequestListPage } from '../src/features/requests/RequestListPage'
import * as apiClient from '../src/app/apiClient'

function row(overrides: Record<string, unknown> = {}) {
  return {
    id: 'a3f1c2d4-0000-4000-8000-000000000001',
    clientName: 'Jane Client',
    dueDate: '2030-04-01',
    status: 'InProgress',
    itemCount: 4,
    approvedCount: 2,
    isOverdue: false,
    createdAt: '2030-03-14T09:30:00Z',
    ...overrides,
  }
}

function page(items: unknown[], total = items.length) {
  return { items, page: 1, pageSize: 20, total }
}

function mockList(response: unknown) {
  return vi.spyOn(apiClient, 'apiRequest').mockResolvedValue(response)
}

function renderList() {
  return render(
    <MemoryRouter>
      <AuthProvider>
        <RequestListPage />
      </AuthProvider>
    </MemoryRouter>,
  )
}

describe('RequestListPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows a loading state until the list arrives (S9.5)', async () => {
    mockList(page([row()]))

    renderList()

    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)
    expect(await screen.findByRole('rowheader', { name: 'Jane Client' })).toBeInTheDocument()
    expect(screen.queryByText(/loading/i)).not.toBeInTheDocument()
  })

  it('shows each request with its approved count (S1.8)', async () => {
    mockList(page([row()]))

    renderList()

    const client = await screen.findByRole('rowheader', { name: 'Jane Client' })
    const cells = within(client.closest('tr')!).getAllByRole('cell')
    expect(cells[1]).toHaveTextContent('In progress')
    expect(cells[2]).toHaveTextContent('2 of 4')
  })

  it('marks an overdue row with text, not colour alone', async () => {
    mockList(
      page([
        row({ id: 'a', clientName: 'Late Client', isOverdue: true }),
        row({ id: 'b', clientName: 'On Time Client' }),
      ]),
    )

    renderList()

    const late = (await screen.findByRole('rowheader', { name: 'Late Client' })).closest('tr')!
    const onTime = screen.getByRole('rowheader', { name: 'On Time Client' }).closest('tr')!

    expect(late).toHaveTextContent(/overdue/i)
    expect(onTime).not.toHaveTextContent(/overdue/i)
  })

  it('shows an empty state when there are no requests', async () => {
    mockList(page([]))

    renderList()

    expect(await screen.findByText(/no requests yet/i)).toBeInTheDocument()
  })

  it('shows a different empty state when filters match nothing', async () => {
    mockList(page([]))

    renderList()
    await userEvent.selectOptions(await screen.findByLabelText('Status'), 'Complete')

    expect(await screen.findByText(/no requests match these filters/i)).toBeInTheDocument()
  })

  it('shows the server message on failure and retries on request', async () => {
    const spy = vi
      .spyOn(apiClient, 'apiRequest')
      .mockRejectedValueOnce(
        new apiClient.ApiError(500, { status: 500, title: 'The server had a problem.' }),
      )
      .mockResolvedValue(page([row()]))

    renderList()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The server had a problem.')
    expect(alert).not.toHaveTextContent('500')

    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('rowheader', { name: 'Jane Client' })).toBeInTheDocument()
    expect(spy).toHaveBeenCalledTimes(2)
  })

  it('reads as an error, not a crash, when the API cannot be reached', async () => {
    vi.spyOn(apiClient, 'apiRequest').mockRejectedValue(new TypeError('Failed to fetch'))

    renderList()

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not reach the server/i)
  })

  it('asks the API for the chosen status, overdue filter and sort (S1.9)', async () => {
    const spy = mockList(page([row()]))

    renderList()
    await screen.findByRole('rowheader', { name: 'Jane Client' })

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'Sent')
    await userEvent.click(screen.getByLabelText('Overdue only'))
    await userEvent.selectOptions(screen.getByLabelText('Sort by'), 'createdAt')

    const lastPath = spy.mock.calls.at(-1)![0]
    const params = new URL(lastPath, 'http://api.test').searchParams
    expect(params.get('status')).toBe('Sent')
    expect(params.get('overdue')).toBe('true')
    expect(params.get('sort')).toBe('createdAt')
    expect(params.get('page')).toBe('1')
  })

  it('pages through the results', async () => {
    const spy = mockList({ items: [row()], page: 1, pageSize: 20, total: 45 })

    renderList()
    expect(await screen.findByText('Page 1 of 3')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Next' }))

    const params = new URL(spy.mock.calls.at(-1)![0], 'http://api.test').searchParams
    expect(params.get('page')).toBe('2')
  })

  it('does not call fetch itself', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch')
    mockList(page([row()]))

    renderList()
    await screen.findByRole('rowheader', { name: 'Jane Client' })

    expect(fetchSpy).not.toHaveBeenCalled()
  })
})
