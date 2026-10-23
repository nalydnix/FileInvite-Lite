import { describe, expect, it, vi, afterEach } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { AuthProvider } from '../src/app/AuthContext'
import { RequestDetailPage } from '../src/features/requests/RequestDetailPage'
import * as apiClient from '../src/app/apiClient'

const ID = 'a3f1c2d4-0000-4000-8000-000000000001'
const LINK = 'https://app.example.com/portal#t=SECRET-TOKEN-VALUE'

function detail(overrides: Record<string, unknown> = {}) {
  return {
    id: ID,
    clientName: 'Jane Client',
    clientEmail: 'jane@example.com',
    dueDate: '2030-04-01',
    status: 'Draft',
    isOverdue: false,
    createdAt: '2030-03-14T09:30:00Z',
    createdBy: { id: 'u1', name: 'Demo Staff' },
    magicLink: null,
    items: [
      {
        id: 'i1',
        title: 'Passport',
        description: 'Photo page',
        required: true,
        status: 'Uploaded',
        latestFile: {
          id: 'f1',
          originalName: 'passport.pdf',
          contentType: 'application/pdf',
          sizeBytes: 100,
          uploadedAt: '2030-03-15T11:02:00Z',
        },
        latestDecision: null,
      },
    ],
    ...overrides,
  }
}

function mockApi(read: unknown) {
  return vi.spyOn(apiClient, 'apiRequest').mockImplementation((_path, options) => {
    if (options?.method === 'POST') {
      return Promise.resolve({ url: LINK, expiresAt: '2030-04-08T09:31:00Z' })
    }
    return Promise.resolve(read)
  })
}

function renderDetail() {
  return render(
    <MemoryRouter initialEntries={[`/requests/${ID}`]}>
      <AuthProvider>
        <Routes>
          <Route path="/requests/:id" element={<RequestDetailPage />} />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  )
}

describe('RequestDetailPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the request, its documents and the latest upload', async () => {
    mockApi(detail())
    renderDetail()

    expect(await screen.findByRole('heading', { name: 'Jane Client' })).toBeInTheDocument()
    const row = screen.getByRole('row', { name: /Passport/ })
    expect(within(row).getByText(/passport\.pdf/)).toBeInTheDocument()
    expect(screen.getByText('No link has been sent yet.')).toBeInTheDocument()
  })

  it('shows the returned link once, with a copy control and a plain warning (S1.11, S1.12)', async () => {
    const spy = mockApi(detail())
    renderDetail()

    await userEvent.click(await screen.findByRole('button', { name: 'Send request' }))

    const post = spy.mock.calls.find(([, options]) => options?.method === 'POST')!
    expect(post[0]).toBe(`/api/requests/${ID}/send`)

    expect(await screen.findByLabelText('Link to send to the client')).toHaveValue(LINK)
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Copy link' })).toBeInTheDocument()
  })

  // A security review found the link sitting in a form control the browser may cache and restore,
  // and that extensions read. The input must opt out, and must not carry a name the browser can
  // key saved form data by.
  it('keeps the link out of browser form-data caching and session restore', async () => {
    mockApi(detail())
    renderDetail()

    await userEvent.click(await screen.findByRole('button', { name: 'Send request' }))

    const input = await screen.findByLabelText('Link to send to the client')
    expect(input).toHaveAttribute('autocomplete', 'off')
    expect(input).not.toHaveAttribute('name')
  })

  it('copies the link to the clipboard and says so', async () => {
    mockApi(detail())
    const user = userEvent.setup()
    const writeText = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue()
    renderDetail()

    await user.click(await screen.findByRole('button', { name: 'Send request' }))
    await user.click(await screen.findByRole('button', { name: 'Copy link' }))

    expect(writeText).toHaveBeenCalledWith(LINK)
    expect(await screen.findByRole('status', { name: '' })).toHaveTextContent('Link copied.')
  })

  it('does not show the link on a later read of the request (S1.12)', async () => {
    mockApi(
      detail({
        status: 'Sent',
        magicLink: {
          issuedAt: '2030-03-14T09:31:00Z',
          expiresAt: '2030-04-08T09:31:00Z',
          revoked: false,
        },
      }),
    )
    renderDetail()

    expect(await screen.findByRole('button', { name: 'Send again' })).toBeInTheDocument()
    expect(screen.queryByLabelText('Link to send to the client')).not.toBeInTheDocument()
    expect(screen.queryByText(/SECRET-TOKEN-VALUE/)).not.toBeInTheDocument()
    expect(screen.getByText(/revokes the current link/i)).toBeInTheDocument()
  })

  it('announces a failed send in the problem detail, not a status code', async () => {
    vi.spyOn(apiClient, 'apiRequest').mockImplementation((_path, options) => {
      if (options?.method === 'POST') {
        return Promise.reject(
          new apiClient.ApiError(400, { detail: 'A request with no items cannot be sent.' }),
        )
      }
      return Promise.resolve(detail())
    })
    renderDetail()

    await userEvent.click(await screen.findByRole('button', { name: 'Send request' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('A request with no items cannot be sent.')
    expect(screen.queryByLabelText('Link to send to the client')).not.toBeInTheDocument()
  })

  it('shows an error with a retry when the request cannot be loaded', async () => {
    vi.spyOn(apiClient, 'apiRequest').mockRejectedValue(
      new apiClient.ApiError(404, { detail: 'The requested resource does not exist.' }),
    )
    renderDetail()

    expect(await screen.findByRole('alert')).toHaveTextContent('The requested resource does not exist.')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})
