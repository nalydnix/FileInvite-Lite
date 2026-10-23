import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { AuthProvider } from '../src/app/AuthContext'
import { LoginPage } from '../src/features/auth/LoginPage'
import * as apiClient from '../src/app/apiClient'

function renderLogin() {
  return render(
    <MemoryRouter>
      <AuthProvider>
        <LoginPage />
      </AuthProvider>
    </MemoryRouter>,
  )
}

describe('LoginPage', () => {
  beforeEach(() => {
    localStorage.clear()
    sessionStorage.clear()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the server message when sign-in fails, not a status code (S9.8)', async () => {
    vi.spyOn(apiClient.api, 'login').mockRejectedValue(
      new apiClient.ApiError(401, {
        status: 401,
        title: 'Unauthorized',
        detail: 'The credentials provided are not valid.',
      }),
    )

    renderLogin()

    await userEvent.type(screen.getByLabelText('Email'), 'demo@test.local')
    await userEvent.type(screen.getByLabelText('Password'), 'wrong')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('The credentials provided are not valid.')
    expect(alert).not.toHaveTextContent('401')
  })

  it('never writes the token to browser storage (S9.2)', async () => {
    vi.spyOn(apiClient.api, 'login').mockResolvedValue({
      token: 'a-token-that-must-not-be-persisted',
      expiresAt: new Date().toISOString(),
      name: 'Demo Staff',
    })

    renderLogin()

    await userEvent.type(screen.getByLabelText('Email'), 'demo@test.local')
    await userEvent.type(screen.getByLabelText('Password'), 'correct')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    const everythingStored = [
      ...Object.values({ ...localStorage }),
      ...Object.values({ ...sessionStorage }),
      document.cookie,
    ].join(' ')

    expect(everythingStored).not.toContain('a-token-that-must-not-be-persisted')
  })

  it('surfaces a readable message when the API cannot be reached', async () => {
    vi.spyOn(apiClient.api, 'login').mockRejectedValue(new TypeError('Failed to fetch'))

    renderLogin()

    await userEvent.type(screen.getByLabelText('Email'), 'demo@test.local')
    await userEvent.type(screen.getByLabelText('Password'), 'whatever')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/could not reach the server/i)
  })
})
