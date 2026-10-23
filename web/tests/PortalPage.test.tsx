import { afterEach, beforeEach, describe, expect, it, vi, type Mock, type MockInstance } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { PortalSessionProvider } from '../src/app/PortalSessionContext'
import { PortalPage } from '../src/features/portal/PortalPage'
import { portalRoutes } from '../src/features/portal/routes'
import * as apiClient from '../src/app/apiClient'

const MAGIC = 'magic-link-token-literal-123'
const SESSION = 'portal-session-token-literal-456'

type Item = Record<string, unknown>

function item(overrides: Item = {}): Item {
  return {
    id: 'item-1',
    title: 'Passport',
    description: 'Photo page',
    required: true,
    status: 'Outstanding',
    ...overrides,
  }
}

function view(items: Item[]) {
  return { clientName: 'Jane Client', dueDate: '2030-04-01', isOverdue: false, items }
}

function arrive(hash = `#t=${MAGIC}`) {
  window.history.replaceState(null, '', `/portal${hash}`)
}

const EXCHANGE_PATH = '/api/portal/exchange'

// Every portal call goes through apiClient, so there is one spy for the page, routed by path
// (web/CLAUDE.md: mock the network at apiClient, not at fetch). The two mocks behind it keep
// per-endpoint call counts assertable, which is what most of these tests turn on.
type Call = (path: string, options?: apiClient.RequestOptions) => Promise<unknown>

let apiSpy: MockInstance
let exchangeMock: Mock<Call>
let portalMock: Mock<Call>

function installApi() {
  exchangeMock = vi.fn()
  portalMock = vi.fn()
  apiSpy = vi
    .spyOn(apiClient, 'apiRequest')
    .mockImplementation((path, options) =>
      path === EXCHANGE_PATH ? exchangeMock(path, options) : portalMock(path, options),
    )
}

function mockExchange() {
  exchangeMock.mockResolvedValue({
    sessionToken: SESSION,
    expiresAt: '2030-03-15T11:30:00Z',
    requestId: 'r1',
  })
  return exchangeMock
}

const UPLOADED = { id: 'f1', originalName: 'passport.pdf', sizeBytes: 3, uploadedAt: 'x' }

function mockPortal(items: Item[], upload?: () => Promise<unknown>) {
  portalMock.mockImplementation((path, options) => {
    if (options?.method === 'POST') {
      return upload ? upload() : Promise.resolve(UPLOADED)
    }
    expect(path).toBe('/api/portal/request')
    return Promise.resolve(view(items))
  })
  return portalMock
}

function renderPortal(strict = false) {
  const tree = (
    <PortalSessionProvider>
      <PortalPage />
    </PortalSessionProvider>
  )
  return render(strict ? <StrictMode>{tree}</StrictMode> : tree)
}

function problem(status: number, detail?: string) {
  return new apiClient.ApiError(status, { status, title: 'Problem', detail })
}

async function pickAndUpload(name = 'passport.pdf') {
  await userEvent.upload(
    screen.getByLabelText(/file for passport/i),
    new File(['abc'], name, { type: 'application/pdf' }),
  )
  await userEvent.click(screen.getByRole('button', { name: /upload/i }))
}

const NEW_LINK = /ask whoever sent you this for a new link/i

describe('PortalPage', () => {
  beforeEach(() => {
    arrive()
    localStorage.clear()
    sessionStorage.clear()
    installApi()
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('exports the portal route', () => {
    expect(portalRoutes.map((r) => r.path)).toEqual(['/portal'])
  })

  it('exchanges the fragment token once and strips it (S9.3)', async () => {
    const exchange = mockExchange()
    mockPortal([item()])

    const { rerender } = renderPortal()
    await screen.findByText('Passport')
    rerender(
      <PortalSessionProvider>
        <PortalPage />
      </PortalSessionProvider>,
    )

    expect(exchange).toHaveBeenCalledTimes(1)
    expect(exchange.mock.calls[0][0]).toBe('/api/portal/exchange')
    expect(exchange.mock.calls[0][1]?.body).toEqual({ token: MAGIC })
    expect(window.location.hash).toBe('')
    expect(window.location.href).not.toContain(MAGIC)
  })

  it('exchanges once even under StrictMode double effects (S9.3)', async () => {
    const exchange = mockExchange()
    mockPortal([item()])

    renderPortal(true)
    await screen.findByText('Passport')

    expect(exchange).toHaveBeenCalledTimes(1)
  })

  it('persists neither token anywhere (S9.4)', async () => {
    mockExchange()
    mockPortal([item()])

    renderPortal()
    await screen.findByText('Passport')

    const stored = JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage })
    for (const secret of [MAGIC, SESSION]) {
      expect(stored).not.toContain(secret)
      expect(document.cookie).not.toContain(secret)
    }
  })

  it('uses the session token, not the link token, for portal calls', async () => {
    mockExchange()
    const portal = mockPortal([item()])

    renderPortal()
    await screen.findByText('Passport')

    expect(portal.mock.calls[0][1]?.token).toBe(SESSION)
  })

  it('shows outstanding, uploaded, approved and rejected distinctly, in text (S9.7)', async () => {
    mockExchange()
    mockPortal([
      item({ id: 'a', title: 'A doc', status: 'Outstanding' }),
      item({ id: 'b', title: 'B doc', status: 'Uploaded' }),
      item({ id: 'c', title: 'C doc', status: 'Approved' }),
      item({
        id: 'd',
        title: 'D doc',
        status: 'Rejected',
        rejectionComment: 'Photo page is cut off',
      }),
    ])

    renderPortal()
    await screen.findByText('A doc')

    const expected: Record<string, RegExp> = {
      'A doc': /outstanding/i,
      'B doc': /uploaded/i,
      'C doc': /approved/i,
      'D doc': /rejected/i,
    }
    for (const [title, status] of Object.entries(expected)) {
      const card = screen.getByRole('heading', { name: title }).closest('li')!
      expect(card).toHaveTextContent(status)
    }
    const rejected = screen.getByRole('heading', { name: 'D doc' }).closest('li')!
    expect(rejected).toHaveTextContent('Photo page is cut off')
    const approved = screen.getByRole('heading', { name: 'C doc' }).closest('li')!
    expect(within(approved).queryByLabelText(/file for/i)).not.toBeInTheDocument()
  })

  it.each([
    [415, 'That file type is not allowed. Use PDF, JPG, PNG, DOCX or XLSX.'],
    [413, 'That file is larger than the 20 MB limit.'],
    [409, 'This document was already approved, so it cannot be replaced.'],
  ])('shows the problem detail for a %i, never the code (S9.8)', async (status, detail) => {
    mockExchange()
    mockPortal([item()], () => Promise.reject(problem(status, detail)))

    renderPortal()
    await screen.findByText('Passport')
    await pickAndUpload()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(detail)
    expect(alert).not.toHaveTextContent(String(status))
  })

  it('falls back to a readable message when the problem has no detail (S9.8)', async () => {
    mockExchange()
    mockPortal([item()], () => Promise.reject(problem(413)))

    renderPortal()
    await screen.findByText('Passport')
    await pickAndUpload()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/too large/i)
    expect(alert).not.toHaveTextContent('413')
  })

  it('shows the ask-for-a-new-link state and no upload controls when exchange fails', async () => {
    exchangeMock.mockRejectedValue(problem(401, 'Invalid link.'))
    const portal = portalMock

    renderPortal()

    expect(await screen.findByText(NEW_LINK)).toBeInTheDocument()
    expect(screen.queryByLabelText(/file for/i)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /upload/i })).not.toBeInTheDocument()
    expect(screen.queryByText(/401/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/password/i)).not.toBeInTheDocument()
    expect(portal).not.toHaveBeenCalled()
  })

  it('shows the same state, without calling the API, when there is no token', async () => {
    arrive('')

    renderPortal()

    expect(await screen.findByText(NEW_LINK)).toBeInTheDocument()
    expect(apiSpy).not.toHaveBeenCalled()
  })

  it('shows loading, empty and error states', async () => {
    mockExchange()
    const portal = portalMock
      .mockRejectedValueOnce(problem(500, 'The server had a problem.'))
      .mockResolvedValue(view([]))

    renderPortal()
    expect(screen.getByRole('status')).toHaveTextContent(/loading/i)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The server had a problem.')

    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }))

    expect(await screen.findByText(/nothing to upload/i)).toBeInTheDocument()
    expect(portal).toHaveBeenCalledTimes(2)
  })

  it('labels the file input and announces a successful upload', async () => {
    mockExchange()
    const portal = mockPortal([item()])

    renderPortal()
    await screen.findByText('Passport')
    expect(screen.getByLabelText(/file for passport/i)).toHaveAttribute('type', 'file')

    await pickAndUpload()

    const announced = await screen.findByText(/uploaded passport\.pdf/i)
    expect(announced.closest('[role="status"]')).not.toBeNull()
    const post = portal.mock.calls.find(([, o]) => o?.method === 'POST')!
    expect(post[0]).toBe('/api/portal/items/item-1/files')
    expect((post[1]?.body as FormData).get('file')).toBeInstanceOf(File)
    // The list is refreshed after the upload.
    expect(portal.mock.calls.filter(([, o]) => o?.method !== 'POST')).toHaveLength(2)
  })

  it('disables the control while uploading and cannot be double-submitted', async () => {
    mockExchange()
    let finish: (value: unknown) => void = () => {}
    const portal = mockPortal([item()], () => new Promise((resolve) => (finish = resolve)))

    renderPortal()
    await screen.findByText('Passport')
    await pickAndUpload()

    const button = screen.getByRole('button', { name: /uploading/i })
    expect(button).toBeDisabled()
    expect(screen.getByLabelText(/file for passport/i)).toBeDisabled()
    await userEvent.click(button)
    await userEvent.keyboard('{Enter}')

    finish(UPLOADED)
    await screen.findByText(/uploaded passport\.pdf/i)

    expect(portal.mock.calls.filter(([, o]) => o?.method === 'POST')).toHaveLength(1)
  })

  it('returns to the new-link state when the session expires mid-upload', async () => {
    mockExchange()
    mockPortal([item()], () => Promise.reject(problem(401)))

    renderPortal()
    await screen.findByText('Passport')
    await pickAndUpload()

    expect(await screen.findByText(NEW_LINK)).toBeInTheDocument()
    expect(screen.queryByLabelText(/file for/i)).not.toBeInTheDocument()
  })

  it('does not call fetch itself', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch')
    mockExchange()
    mockPortal([item()])

    renderPortal()
    await screen.findByText('Passport')

    expect(fetchSpy).not.toHaveBeenCalled()
  })
})
