namespace Nyx.Desktop.Core.AccountStatus;

/// <summary>Shared official Star Rail record signing for the existing browser-session readers.</summary>
public static class HoyoLabHsrRequestScript
{
    public const string Signer = """
        // HSR_DS_SIGNER_START
        const HSR_DS_INVALID = Symbol('invalid-hsr-signature');
        const HSR_DS_SALT = '6s25p5ox5y14umn1p61aqyyvbvvl3lrt';
        const HSR_DS_RANDOM_LENGTH = 6;
        const HSR_DS_ALPHABET = 'abcdefghijklmnopqrstuvwxyz';
        const HSR_MD5_SHIFTS = Object.freeze([
          7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
          5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
          4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
          6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
        ]);
        const HSR_MD5_CONSTANTS = Object.freeze([
          0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
          0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
          0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
          0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
          0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
          0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
          0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
          0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
          0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
          0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
          0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
          0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
          0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
          0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
          0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
          0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
        ]);

        function hsrMd5Ascii(value) {
          if (typeof value !== 'string'
            || value.length === 0
            || value.length > 128
            || /[^\x20-\x7e]/.test(value))
            throw HSR_DS_INVALID;

          const paddedLength = Math.ceil((value.length + 9) / 64) * 64;
          const bytes = new Uint8Array(paddedLength);
          try {
            for (let index = 0; index < value.length; index += 1) {
              bytes[index] = value.charCodeAt(index);
            }
            bytes[value.length] = 0x80;
            const view = new DataView(bytes.buffer);
            view.setUint32(paddedLength - 8, value.length * 8, true);
            view.setUint32(paddedLength - 4, 0, true);

            let stateA = 0x67452301;
            let stateB = 0xefcdab89;
            let stateC = 0x98badcfe;
            let stateD = 0x10325476;
            for (let offset = 0; offset < paddedLength; offset += 64) {
              let a = stateA;
              let b = stateB;
              let c = stateC;
              let d = stateD;
              for (let round = 0; round < 64; round += 1) {
                let mixed;
                let word;
                if (round < 16) {
                  mixed = (b & c) | (~b & d);
                  word = round;
                } else if (round < 32) {
                  mixed = (d & b) | (~d & c);
                  word = (5 * round + 1) % 16;
                } else if (round < 48) {
                  mixed = b ^ c ^ d;
                  word = (3 * round + 5) % 16;
                } else {
                  mixed = c ^ (b | ~d);
                  word = (7 * round) % 16;
                }
                const sum = (
                  a
                  + mixed
                  + HSR_MD5_CONSTANTS[round]
                  + view.getUint32(offset + word * 4, true)) >>> 0;
                const shift = HSR_MD5_SHIFTS[round];
                const rotated = ((sum << shift) | (sum >>> (32 - shift))) >>> 0;
                const previousD = d;
                d = c;
                c = b;
                b = (b + rotated) >>> 0;
                a = previousD;
              }
              stateA = (stateA + a) >>> 0;
              stateB = (stateB + b) >>> 0;
              stateC = (stateC + c) >>> 0;
              stateD = (stateD + d) >>> 0;
            }

            let digest = '';
            for (const word of [stateA, stateB, stateC, stateD]) {
              for (let byte = 0; byte < 4; byte += 1) {
                digest += ((word >>> (byte * 8)) & 0xff)
                  .toString(16)
                  .padStart(2, '0');
              }
            }
            return digest;
          } finally {
            bytes.fill(0);
          }
        }

        function hsrRandom() {
          if (!globalThis.crypto
            || typeof globalThis.crypto.getRandomValues !== 'function')
            throw HSR_DS_INVALID;
          const sample = new Uint8Array(1);
          const characters = [];
          try {
            while (characters.length < HSR_DS_RANDOM_LENGTH) {
              globalThis.crypto.getRandomValues(sample);
              if (sample[0] >= 234) continue;
              characters.push(HSR_DS_ALPHABET[sample[0] % HSR_DS_ALPHABET.length]);
            }
            return characters.join('');
          } finally {
            sample.fill(0);
            characters.length = 0;
          }
        }

        function hsrNoteHeaders() {
          if (hsrMd5Ascii(
            'salt=6s25p5ox5y14umn1p61aqyyvbvvl3lrt&t=1700000000&r=abcdef')
            !== '52ac4768378434146675f980be7d092a')
            throw HSR_DS_INVALID;
          const timestamp = Math.floor(Date.now() / 1000);
          if (!Number.isSafeInteger(timestamp)
            || timestamp < 1600000000
            || timestamp > 4102444800)
            throw HSR_DS_INVALID;
          const random = hsrRandom();
          if (random.length !== HSR_DS_RANDOM_LENGTH
            || !/^[a-z]{6}$/.test(random))
            throw HSR_DS_INVALID;
          const material = 'salt=' + HSR_DS_SALT + '&t=' + timestamp + '&r=' + random;
          const signature = hsrMd5Ascii(material);
          if (!/^[a-f0-9]{32}$/.test(signature)) throw HSR_DS_INVALID;
          const ds = timestamp + ',' + random + ',' + signature;
          if (!/^[0-9]{10},[a-z]{6},[a-f0-9]{32}$/.test(ds)) throw HSR_DS_INVALID;
          return Object.freeze({
            'x-rpc-client_type': '5',
            'x-rpc-app_version': '1.5.0',
            'x-rpc-language': 'en-us',
            DS: timestamp + ',' + random + ',' + signature,
          });
        }
        // HSR_DS_SIGNER_END
        """;
}
