//! Shared, test-only protobuf checks for candidate command bodies.

use protobuf::{CodedInputStream, rt::WireType};

#[derive(Debug, Eq, PartialEq)]
pub(super) enum WireError {
    UnsupportedCommand,
    BodyTooLarge,
    TooManyRows,
    Malformed,
}

impl From<protobuf::Error> for WireError {
    fn from(_: protobuf::Error) -> Self {
        // Do not retain parser errors that might contain a decoded input value.
        Self::Malformed
    }
}

pub(super) fn tag(input: &mut CodedInputStream<'_>) -> Result<(u32, WireType), WireError> {
    let raw = u32::try_from(input.read_raw_varint64()?).map_err(|_| WireError::Malformed)?;
    let field = raw >> 3;
    if field == 0 {
        return Err(WireError::Malformed);
    }
    let wire = WireType::new(raw & 7).ok_or(WireError::Malformed)?;
    Ok((field, wire))
}

pub(super) fn require_wire(actual: WireType, expected: WireType) -> Result<(), WireError> {
    if actual != expected {
        return Err(WireError::Malformed);
    }
    Ok(())
}

pub(super) fn once(seen: &mut u32, field: u32) -> Result<(), WireError> {
    // Only explicitly matched scalar fields 1..=14 call this helper.
    let bit = 1 << field;
    if *seen & bit != 0 {
        return Err(WireError::Malformed);
    }
    *seen |= bit;
    Ok(())
}

pub(super) fn scalar(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
    seen: &mut u32,
    field: u32,
) -> Result<u64, WireError> {
    require_wire(wire, WireType::Varint)?;
    once(seen, field)?;
    Ok(input.read_uint64()?)
}

pub(super) fn uint32(
    input: &mut CodedInputStream<'_>,
    wire: WireType,
    seen: &mut u32,
    field: u32,
) -> Result<u32, WireError> {
    u32::try_from(scalar(input, wire, seen, field)?).map_err(|_| WireError::Malformed)
}

pub(super) fn length(input: &mut CodedInputStream<'_>, wire: WireType) -> Result<u32, WireError> {
    require_wire(wire, WireType::LengthDelimited)?;
    u32::try_from(input.read_raw_varint64()?).map_err(|_| WireError::Malformed)
}

pub(super) fn skip(input: &mut CodedInputStream<'_>, wire: WireType) -> Result<(), WireError> {
    match wire {
        WireType::StartGroup | WireType::EndGroup => return Err(WireError::Malformed),
        WireType::LengthDelimited => {
            let len = length(input, wire)?;
            input.skip_raw_bytes(len)?;
        }
        _ => input.skip_field(wire)?,
    }
    Ok(())
}
