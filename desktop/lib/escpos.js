'use strict';

class EscPos {
  constructor() {
    this.buf = Buffer.alloc(0);
  }

  raw(data) {
    this.buf = Buffer.concat([this.buf, Buffer.from(data)]);
    return this;
  }

  init() {
    return this.raw([0x1b, 0x40]);
  }

  setCharset() {
    return this.raw([0x1b, 0x74, 0x00]);
  }

  align(align) {
    return this.raw([0x1b, 0x61, align]);
  }

  fontSize(size) {
    return this.raw([0x1d, 0x21, size]);
  }

  enableBold(enable) {
    return this.raw([0x1b, 0x45, enable ? 1 : 0]);
  }

  enableUnderline(enable) {
    return this.raw([0x1b, 0x2d, enable ? 1 : 0]);
  }

  text(value) {
    return this.raw(Buffer.from(value, 'latin1'));
  }

  line(value = '') {
    this.text(value);
    return this.raw([0x0a]);
  }

  separator(char = '-', width = 32) {
    return this.line(char.repeat(width));
  }

  feed(lines) {
    return this.raw([0x1b, 0x64, lines]);
  }

  cut() {
    return this.raw([0x1d, 0x56, 0x42, 0x00]);
  }

  openDrawer() {
    return this.raw([0x1b, 0x70, 0x00, 0x19, 0x32]);
  }

  qrCode(content, size = 8) {
    const data = Buffer.from(content, 'latin1');
    const lowHigh = (v) => [v & 0xff, (v >> 8) & 0xff];
    this.raw([0x1d, 0x28, 0x6b, ...lowHigh(3), 0x00, 0x31, 0x43, size]);
    this.raw([0x1d, 0x28, 0x6b, ...lowHigh(3), 0x00, 0x31, 0x45, 0x32]);
    this.raw([0x1d, 0x28, 0x6b, ...lowHigh(data.length + 3), 0x00, 0x31, 0x50, 0x30]);
    this.raw(data);
    this.raw([0x1d, 0x28, 0x6b, ...lowHigh(3), 0x00, 0x31, 0x51, 0x30]);
    return this;
  }

  barcodeEan13(content, height = 60) {
    const value = content.padEnd(13, '0').substring(0, 13);
    this.raw([0x1d, 0x68, height]);
    this.raw([0x1d, 0x48, 0x02]);
    this.raw([0x1d, 0x77, 0x02]);
    this.raw([0x1d, 0x6b, 67]);
    this.raw(Buffer.from(value, 'ascii'));
    return this;
  }

  get bytes() {
    return this.buf;
  }
}

const Align = { LEFT: 0, CENTER: 1, RIGHT: 2 };
const FontSize = { NORMAL: 0, DOUBLE_WIDTH: 1, DOUBLE_HEIGHT: 2, DOUBLE_BOTH: 3 };

function buildTestReceipt(printerName) {
  const pos = new EscPos();
  pos.init()
    .setCharset()
    .align(Align.CENTER)
    .fontSize(FontSize.DOUBLE_BOTH)
    .enableBold(true)
    .line('TESTE RAW BTS')
    .enableBold(false)
    .fontSize(FontSize.NORMAL)
    .line()
    .line('Impressora: ' + printerName)
    .line()
    .line('Impressao de teste via')
    .line('Bluetooth / USB / Rede')
    .line()
    .align(Align.LEFT)
    .line('--------------------------------')
    .line('Produto                Valor')
    .line('--------------------------------')
    .line('TESOURA SEM FIO       R$ 34,90')
    .line('CABO USB 2M           R$ 12,00')
    .line('CANETA AZUL            R$ 2,50')
    .line('--------------------------------')
    .line('TOTAL                 R$ 49,40')
    .line()
    .align(Align.CENTER)
    .qrCode('RAWBTS-TESTE-01', 8)
    .line()
    .line('Codigo QR gerado')
    .line('pelo motor ESC/POS')
    .line()
    .align(Align.LEFT)
    .barcodeEan13('7891234567890')
    .line()
    .line('Obrigado pela preferencia!')
    .feed(4)
    .cut();
  return pos.bytes;
}

module.exports = { EscPos, Align, FontSize, buildTestReceipt };
