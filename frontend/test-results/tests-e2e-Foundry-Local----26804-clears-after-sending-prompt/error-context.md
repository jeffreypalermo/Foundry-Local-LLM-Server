# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: tests\e2e.spec.ts >> Foundry Local - E2E Exploratory Testing >> Test 7: Textarea clears after sending prompt
- Location: tests\e2e.spec.ts:184:3

# Error details

```
Test timeout of 30000ms exceeded.
```

```
Error: locator.click: Test timeout of 30000ms exceeded.
Call log:
  - waiting for locator('button:has-text("Send Prompt")')
    - locator resolved to <button disabled type="submit">Send Prompt</button>
  - attempting click action
    2 × waiting for element to be visible, enabled and stable
      - element is not enabled
    - retrying click action
    - waiting 20ms
    2 × waiting for element to be visible, enabled and stable
      - element is not enabled
    - retrying click action
      - waiting 100ms
    57 × waiting for element to be visible, enabled and stable
       - element is not enabled
     - retrying click action
       - waiting 500ms

```

# Page snapshot

```yaml
- main [ref=e3]:
  - heading "Foundry Local OpenAI Server" [level=1] [ref=e4]
  - paragraph [ref=e5]:
    - text: "Model:"
    - strong [ref=e6]: loading...
  - paragraph [ref=e7]:
    - text: "Foundry endpoint:"
    - code [ref=e8]: loading...
  - paragraph [ref=e9]:
    - text: "OpenAI-compatible endpoint for tools:"
    - code [ref=e10]: /v1/chat/completions
  - generic [ref=e11]:
    - textbox "Enter a prompt" [active] [ref=e12]: Test message to verify textarea clears
    - button "Send Prompt" [disabled] [ref=e13] [cursor=pointer]
  - paragraph [ref=e14]: Could not load Foundry Local settings (502)
  - region "Chat transcript"
```

# Test source

```ts
  94  |     console.log('\n✓ TEST 4: Multiple prompts in conversation');
  95  | 
  96  |     await page.goto(frontendUrl);
  97  |     await expect(page.locator('textarea')).toBeVisible();
  98  | 
  99  |     const prompts = [
  100 |       'Hello!',
  101 |       'How do local LLMs work?',
  102 |       'What can I use them for?'
  103 |     ];
  104 | 
  105 |     for (const prompt of prompts) {
  106 |       const textarea = page.locator('textarea');
  107 |       await textarea.fill(prompt);
  108 | 
  109 |       const button = page.locator('button:has-text("Send Prompt")');
  110 |       await button.click();
  111 | 
  112 |       // Wait a moment for response
  113 |       await page.waitForTimeout(500);
  114 |       console.log(`  - Sent: "${prompt}"`);
  115 |     }
  116 | 
  117 |     // Verify all messages are in the chat
  118 |     const messages = page.locator('article.message');
  119 |     const messageCount = await messages.count();
  120 |     console.log(`  - Total messages in chat: ${messageCount}`);
  121 |     // Should have at least 6 (3 user + 3 assistant)
  122 |     expect(messageCount).toBeGreaterThanOrEqual(prompts.length);
  123 |   });
  124 | 
  125 |   test('Test 5: Verify API response format is correct', async ({ page }) => {
  126 |     console.log('\n✓ TEST 5: Verify API response format');
  127 | 
  128 |     const requestBody = {
  129 |       model: 'gemma4:latest',
  130 |       messages: [
  131 |         { role: 'user', content: 'Test message for API format verification' }
  132 |       ]
  133 |     };
  134 | 
  135 |     const response = await fetch(`${serverUrl}/v1/chat/completions`, {
  136 |       method: 'POST',
  137 |       headers: { 'Content-Type': 'application/json' },
  138 |       body: JSON.stringify(requestBody)
  139 |     });
  140 | 
  141 |     expect(response.ok).toBe(true);
  142 |     console.log('  - API returned 200 OK');
  143 | 
  144 |     const data = await response.json();
  145 |     expect(data.choices).toBeDefined();
  146 |     expect(data.choices.length).toBeGreaterThan(0);
  147 |     expect(data.choices[0].message).toBeDefined();
  148 |     expect(data.choices[0].message.content).toBeDefined();
  149 |     expect(data.model).toBeDefined();
  150 |     expect(data.usage).toBeDefined();
  151 | 
  152 |     console.log(`  - Response structure valid`);
  153 |     console.log(`  - Model: ${data.model}`);
  154 |     console.log(`  - Choice 0 content: "${data.choices[0].message.content.substring(0, 50)}..."`);
  155 |   });
  156 | 
  157 |   test('Test 6: Clear previous messages and start fresh', async ({ page }) => {
  158 |     console.log('\n✓ TEST 6: Reload and verify fresh start');
  159 | 
  160 |     // First load
  161 |     await page.goto(frontendUrl);
  162 |     const textarea = page.locator('textarea');
  163 |     await textarea.fill('First conversation');
  164 |     const button = page.locator('button:has-text("Send Prompt")');
  165 |     await button.click();
  166 |     await page.waitForTimeout(500);
  167 | 
  168 |     // Verify message exists
  169 |     let messages = page.locator('article.message');
  170 |     const countBefore = await messages.count();
  171 |     console.log(`  - Before reload: ${countBefore} messages`);
  172 | 
  173 |     // Reload page
  174 |     await page.reload();
  175 |     await expect(textarea).toBeVisible({ timeout: 5000 });
  176 | 
  177 |     // Verify chat is cleared (only should be empty or have default state)
  178 |     messages = page.locator('article.message');
  179 |     const countAfter = await messages.count();
  180 |     console.log(`  - After reload: ${countAfter} messages`);
  181 |     expect(countAfter).toBeLessThan(countBefore);
  182 |   });
  183 | 
  184 |   test('Test 7: Textarea clears after sending prompt', async ({ page }) => {
  185 |     console.log('\n✓ TEST 7: Textarea clears after sending');
  186 | 
  187 |     await page.goto(frontendUrl);
  188 |     const textarea = page.locator('textarea');
  189 | 
  190 |     const testPrompt = 'Test message to verify textarea clears';
  191 |     await textarea.fill(testPrompt);
  192 | 
  193 |     const button = page.locator('button:has-text("Send Prompt")');
> 194 |     await button.click();
      |                  ^ Error: locator.click: Test timeout of 30000ms exceeded.
  195 | 
  196 |     // Wait for response
  197 |     await page.waitForSelector('article.message.assistant p');
  198 | 
  199 |     // Verify textarea is cleared
  200 |     const textareaValue = await textarea.inputValue();
  201 |     console.log(`  - Textarea after send: "${textareaValue}"`);
  202 |     expect(textareaValue).toBe('');
  203 |   });
  204 | });
  205 | 
```