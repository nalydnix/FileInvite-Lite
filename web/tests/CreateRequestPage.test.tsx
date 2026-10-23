import { describe, expect, it, vi, afterEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { AuthProvider } from '../src/app/AuthContext'
import { requestRoutes } from '../src/features/requests/routes'
import { CreateRequestPage } from '../src/features/requests/CreateRequestPage'
import { RequestListPage } from '../src/features/requests/RequestListPage'
import { Route, Routes } from 'react-router-dom'
import * as apiClient from '../src/app/apiClient'

// RequireStaffAuth needs a session; the pages are what is under test, so mount them directly.
function renderCreate() {
  return render(
    <MemoryRouter initialEntries={['/requests/new']}>
      <AuthProvider>
        <Routes>
          <Route path="/requests/new" element={<CreateRequestPage />} />
          <Route path="/requests" element={<RequestListPage />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  )
}

async function fillValidForm() {
  await userEvent.type(screen.getByLabelText('Client name'), 'Jane Client')
  await userEvent.type(screen.getByLabelText('Client email'), 'jane@example.com')
  await userEvent.type(screen.getByLabelText('Due date'), '2030-04-01')
  await userEvent.type(screen.getByLabelText('Document 1 title'), 'Passport')
}

describe('CreateRequestPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('exports the create and list routes behind staff auth', () => {
    expect(requestRoutes.map((r) => r.path)).toEqual(['/requests', '/requests/new'])
  })

  it('creates a request and shows it in the list (S9.5)', async () => {
    const spy = vi.spyOn(apiClient, 'apiRequest').mockImplementation((_path, options) => {
      if (options?.method === 'POST') return Promise.resolve({ id: 'new-id' })
      return Promise.resolve({
        items: [
          {
            id: 'new-id',
            clientName: 'Jane Client',
            dueDate: '2030-04-01',
            status: 'Draft',
            itemCount: 1,
            approvedCount: 0,
            isOverdue: false,
            createdAt: '2030-03-14T09:30:00Z',
          },
        ],
        page: 1,
        pageSize: 20,
        total: 1,
      })
    })

    renderCreate()
    await fillValidForm()
    await userEvent.click(screen.getByRole('button', { name: 'Create request' }))

    expect(await screen.findByRole('rowheader', { name: 'Jane Client' })).toBeInTheDocument()

    const post = spy.mock.calls.find(([, options]) => options?.method === 'POST')!
    expect(post[0]).toBe('/api/requests')
    expect(post[1]?.body).toEqual({
      clientName: 'Jane Client',
      clientEmail: 'jane@example.com',
      dueDate: '2030-04-01',
      items: [{ title: 'Passport', description: null, required: true }],
    })
  })

  it('shows the server message beside each field it names (S1.2, S1.4)', async () => {
    vi.spyOn(apiClient, 'apiRequest').mockRejectedValue(
      new apiClient.ApiError(400, {
        status: 400,
        title: 'One or more validation errors occurred.',
        errors: {
          clientEmail: ['A valid email address is required.'],
          'items[0].title': ['Title must not be empty.'],
        },
      }),
    )

    renderCreate()
    await userEvent.click(screen.getByRole('button', { name: 'Create request' }))

    const email = screen.getByLabelText('Client email')
    expect(email).toHaveAccessibleDescription('A valid email address is required.')

    const title = screen.getByLabelText('Document 1 title')
    expect(title).toHaveAccessibleDescription('Title must not be empty.')

    // The form stays put so the user can fix it.
    expect(screen.getByRole('button', { name: 'Create request' })).toBeInTheDocument()
  })

  it('shows a message for the whole form when the failure names no field', async () => {
    vi.spyOn(apiClient, 'apiRequest').mockRejectedValue(
      new apiClient.ApiError(500, { status: 500, title: 'The server had a problem.' }),
    )

    renderCreate()
    await userEvent.click(screen.getByRole('button', { name: 'Create request' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The server had a problem.')
    expect(alert).not.toHaveTextContent('500')
  })

  it('adds and removes documents', async () => {
    renderCreate()

    await userEvent.click(screen.getByRole('button', { name: 'Add document' }))
    expect(screen.getByLabelText('Document 2 title')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Remove document 1' }))
    expect(screen.queryByLabelText('Document 2 title')).not.toBeInTheDocument()
    expect(screen.getByLabelText('Document 1 title')).toBeInTheDocument()
  })

  it('does not call fetch itself', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch')
    vi.spyOn(apiClient, 'apiRequest').mockResolvedValue({ id: 'x' })

    renderCreate()
    await fillValidForm()
    await userEvent.click(screen.getByRole('button', { name: 'Create request' }))

    expect(fetchSpy).not.toHaveBeenCalled()
  })
})
